[CmdletBinding()]
param(
    [string] $WorkflowRoot = (Join-Path $PSScriptRoot "../.github/workflows")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This deliberately accepts the repository's small, literal workflow subset. An upload
# using an unfamiliar YAML shape or a computed path must fail until it is reviewed.
function Get-StepScalar {
    param([object] $Step, [string] $Key)

    $escaped = [regex]::Escape($Key)
    foreach ($line in $Step.Lines) {
        if ($line -match "^      -\s+${escaped}:\s*(.*)$" -or
            $line -match "^        ${escaped}:\s*(.*)$") {
            return $Matches[1].Trim()
        }
    }
    return $null
}

function Normalize-Condition {
    param([string] $Value)

    if ($null -eq $Value) { return "" }
    $condition = $Value.Trim().Trim("'", '"').Trim()
    if ($condition.StartsWith('${{') -and $condition.EndsWith('}}')) {
        $condition = $condition.Substring(3, $condition.Length - 5).Trim()
    }
    return [regex]::Replace($condition, '\s+', ' ')
}

function Assert-LiteralArtifactPath {
    param([string] $Value, [string] $Context, [bool] $AllowGlob)

    $path = $Value.Trim().Trim("'", '"').Trim()
    if (-not $path.StartsWith('artifacts/', [StringComparison]::Ordinal) -or
        $path.Contains('\') -or $path.Contains('`') -or $path.Contains("`n") -or
        $path.Contains("`r") -or $path.StartsWith('!') -or
        $path.Split('/') -contains '.' -or $path.Split('/') -contains '..' -or
        ($path -match '[*?\[]' -and -not $AllowGlob)) {
        throw "$Context has an unsupported artifact path."
    }
    if ($path -match '\$\{\{(?!\s*(?:github\.run_id|github\.run_attempt|matrix\.(?:stage|engine))\s*\}\})') {
        throw "$Context has an unreviewed path expression."
    }
    return $path.TrimEnd('/')
}

function Get-RunLines {
    param([object] $Step)

    $start = -1
    for ($index = 0; $index -lt $Step.Lines.Count; $index++) {
        if ($Step.Lines[$index] -match '^        run:\s*\|[-+]?\s*$') {
            $start = $index + 1
            break
        }
    }
    if ($start -lt 0) { throw "$($Step.Location) must use a literal run block." }

    $result = [Collections.Generic.List[string]]::new()
    for ($index = $start; $index -lt $Step.Lines.Count; $index++) {
        $line = $Step.Lines[$index]
        if ($line -match '^        [A-Za-z][A-Za-z0-9_-]*:') { break }
        if ($line.Trim().Length -gt 0) {
            if ($line -notmatch '^\s{10,}\S') {
                throw "$($Step.Location) has an unsupported scan command indentation."
            }
            $result.Add($line.Trim())
        }
    }
    return $result.ToArray()
}

function Get-ScanPaths {
    param([object] $Step)

    $inline = Get-StepScalar $Step 'run'
    if ($null -ne $inline -and $inline -ne '|') {
        $prefix = 'pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 '
        if (-not $inline.StartsWith($prefix, [StringComparison]::Ordinal)) {
            throw "$($Step.Location) must invoke eng/Scan-ArtifactSecrets.ps1 directly."
        }
        return Assert-LiteralArtifactPath ($inline.Substring($prefix.Length)) $Step.Location $false
    }

    $lines = @(Get-RunLines $Step)
    if ($lines.Count -lt 2 -or
        $lines[0].TrimEnd('`').Trim() -cne 'pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1') {
        throw "$($Step.Location) must invoke eng/Scan-ArtifactSecrets.ps1 directly."
    }

    $paths = [Collections.Generic.List[string]]::new()
    foreach ($line in $lines[1..($lines.Count - 1)]) {
        $argument = $line.TrimEnd('`').Trim()
        if ($argument.Length -eq 0) { throw "$($Step.Location) has an empty scan argument." }
        $paths.Add((Assert-LiteralArtifactPath $argument $Step.Location $false))
    }
    return $paths.ToArray()
}

function Get-UploadPaths {
    param([object] $Step)

    $with = $false
    for ($index = 0; $index -lt $Step.Lines.Count; $index++) {
        $line = $Step.Lines[$index]
        if ($line -match '^        with:\s*$') { $with = $true; continue }
        if ($line -match '^        [A-Za-z][A-Za-z0-9_-]*:' -and $line -notmatch '^        with:') {
            $with = $false
        }
        if (-not $with -or $line -notmatch '^          path:\s*(.*)$') { continue }

        $value = $Matches[1].Trim()
        $paths = [Collections.Generic.List[string]]::new()
        if ($value -match '^[|>][-+]?\s*$') {
            for ($next = $index + 1; $next -lt $Step.Lines.Count; $next++) {
                $item = $Step.Lines[$next]
                if ($item.Trim().Length -eq 0) { continue }
                if ($item -notmatch '^            \S') { break }
                $paths.Add((Assert-LiteralArtifactPath $item.Trim() $Step.Location $true))
            }
        } else {
            $paths.Add((Assert-LiteralArtifactPath $value $Step.Location $true))
        }
        if ($paths.Count -eq 0) { throw "$($Step.Location) has no upload paths." }
        return $paths.ToArray()
    }
    throw "$($Step.Location) has no literal with.path."
}

function Test-ScanCoversUpload {
    param([string] $ScanPath, [string] $UploadPath)

    $glob = [regex]::Match($UploadPath, '[*?\[]')
    if ($glob.Success) {
        $lastSlash = $UploadPath.LastIndexOf('/', $glob.Index)
        if ($lastSlash -lt 0) { return $false }
        $uploadBase = $UploadPath.Substring(0, $lastSlash)
    } else {
        $uploadBase = $UploadPath
    }
    return $uploadBase -ceq $ScanPath -or
        $uploadBase.StartsWith($ScanPath + '/', [StringComparison]::Ordinal)
}

function Assert-JobUploads {
    param([object] $Job)

    $steps = @($Job.Steps)
    $scanIndices = @(
        for ($index = 0; $index -lt $steps.Count; $index++) {
            if ((Get-StepScalar $steps[$index] 'id') -ceq 'artifact_scan') { $index }
        }
    )
    $uploadIndices = @(
        for ($index = 0; $index -lt $steps.Count; $index++) {
            $uses = Get-StepScalar $steps[$index] 'uses'
            if ($null -ne $uses -and $uses -match '^[''\"]?actions/upload-artifact@') { $index }
        }
    )
    if ($uploadIndices.Count -eq 0) { return 0 }
    if ($scanIndices.Count -ne 1) {
        throw "$($Job.Location) must have exactly one artifact_scan step for its uploads."
    }

    $scanIndex = $scanIndices[0]
    $scan = $steps[$scanIndex]
    if ($scanIndex -ne $uploadIndices[0] - 1) {
        throw "$($scan.Location) must immediately precede the first upload."
    }
    if ((Normalize-Condition (Get-StepScalar $scan 'if')) -cne 'always()') {
        throw "$($scan.Location) must run with if: always()."
    }
    $continueOnError = Get-StepScalar $scan 'continue-on-error'
    if ($null -ne $continueOnError -and $continueOnError -cne 'false') {
        throw "$($scan.Location) must fail the job when scanning fails."
    }
    $scanPaths = @(Get-ScanPaths $scan)

    for ($index = $scanIndex + 1; $index -lt $steps.Count; $index++) {
        if ($uploadIndices -cnotcontains $index) {
            throw "$($steps[$index].Location) must not run after artifact_scan and uploads."
        }
    }
    foreach ($index in $uploadIndices) {
        $upload = $steps[$index]
        if ((Normalize-Condition (Get-StepScalar $upload 'if')) -cne
            "always() && steps.artifact_scan.outcome == 'success'") {
            throw "$($upload.Location) must require artifact_scan.outcome == 'success'."
        }
        foreach ($path in @(Get-UploadPaths $upload)) {
            if (-not @($scanPaths | Where-Object { Test-ScanCoversUpload $_ $path }).Count) {
                throw "$($upload.Location) uploads a path outside artifact_scan scope."
            }
        }
    }
    return $uploadIndices.Count
}

function New-Job {
    param([string] $Location)
    return [pscustomobject]@{ Location = $Location; Steps = [Collections.Generic.List[object]]::new() }
}

function Assert-Workflow {
    param([string] $Path)

    $lines = [IO.File]::ReadAllLines($Path)
    $jobs = [Collections.Generic.List[object]]::new()
    $job = $null
    $step = $null
    $inJobs = $false
    $inSteps = $false
    $rawUploads = 0

    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if ($line -match 'actions/upload-artifact@' -and $line -notmatch '^\s*#') { $rawUploads++ }
        if ($line -match '^jobs:\s*(?:#.*)?$') { $inJobs = $true; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^[^\s#]' -and $line -notmatch '^jobs:') { $inJobs = $false; continue }
        if ($line -match '^  [A-Za-z0-9_-]+:\s*(?:#.*)?$') {
            if ($null -ne $job) { $jobs.Add($job) }
            $job = New-Job "${Path}:$($index + 1)"
            $step = $null
            $inSteps = $false
            continue
        }
        if ($null -eq $job) { continue }
        if ($line -match '^    steps:\s*(?:#.*)?$') { $inSteps = $true; continue }
        if (-not $inSteps) { continue }
        if ($line -match '^      -(?:\s|$)') {
            $step = [pscustomobject]@{
                Location = "${Path}:$($index + 1)"
                Lines = [Collections.Generic.List[string]]::new()
            }
            $job.Steps.Add($step)
        }
        if ($null -ne $step) { $step.Lines.Add($line) }
    }
    if ($null -ne $job) { $jobs.Add($job) }

    $checked = 0
    foreach ($item in $jobs) { $checked += Assert-JobUploads $item }
    if ($checked -ne $rawUploads) {
        throw "$Path contains an upload outside the supported job/step structure."
    }
    return $checked
}

$fullRoot = [IO.Path]::GetFullPath($WorkflowRoot)
if (-not [IO.Directory]::Exists($fullRoot)) { throw "Workflow root is unavailable." }
$files = @([IO.Directory]::GetFiles($fullRoot, '*.yml') + [IO.Directory]::GetFiles($fullRoot, '*.yaml') |
    Sort-Object -Unique)
if ($files.Count -eq 0) { throw "Workflow root has no YAML files." }

$count = 0
foreach ($file in $files) { $count += Assert-Workflow $file }
if ($count -eq 0) { throw "No artifact upload steps were checked." }
Write-Host "Artifact upload policy passed for $count upload steps in $($files.Count) workflows."
