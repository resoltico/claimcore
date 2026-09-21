[CmdletBinding()]
param(
    [string] $Root = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$workflows = Join-Path $Root ".github/workflows"
$actions = Join-Path $Root ".github/actions"

if (-not [IO.Directory]::Exists($workflows)) {
    throw "The workflow directory is missing."
}

$failures = [Collections.Generic.List[string]]::new()

$workflowFiles = @(Get-ChildItem -LiteralPath $workflows -Filter *.yml -File | Sort-Object Name)
if ($workflowFiles.Count -eq 0) {
    throw "No workflows were found to check."
}

$actionFiles = @()
if ([IO.Directory]::Exists($actions)) {
    $actionFiles = @(Get-ChildItem -LiteralPath $actions -Filter action.yml -File -Recurse | Sort-Object FullName)
}

# A toolchain selected in one workflow and not another is how a runner quietly builds ClaimCore with
# a different SDK or npm. The composite action is the single place that selects and proves both.
foreach ($file in $workflowFiles) {
    $lines = [IO.File]::ReadAllLines($file.FullName)

    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]

        if ($line -match 'uses:\s*actions/setup-(dotnet|node)@') {
            $failures.Add("$($file.Name):$($index + 1) selects a toolchain directly; use ./.github/actions/toolchain.")
        }

        if ($line -match 'uses:\s*actions/checkout@') {
            $window = $lines[$index..([Math]::Min($index + 6, $lines.Length - 1))] -join "`n"
            if ($window -notmatch 'persist-credentials:\s*false') {
                $failures.Add("$($file.Name):$($index + 1) checks out without persist-credentials: false.")
            }
        }
    }
}

# Every third-party action, in workflows and in composite actions alike, stays pinned to a full
# commit with a reviewed version comment.
foreach ($file in ($workflowFiles + $actionFiles)) {
    $relative = $file.FullName.Substring($Root.Length).TrimStart([char]"/", [char]"\")
    $lines = [IO.File]::ReadAllLines($file.FullName)

    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]

        if ($line -notmatch 'uses:\s*(\S+)') { continue }

        $reference = $Matches[1]
        if ($reference.StartsWith("./")) { continue }

        if ($reference -notmatch '^[^@]+@[0-9a-f]{40}$') {
            $failures.Add("${relative}:$($index + 1) uses an unpinned action reference '$reference'.")
        }
        elseif ($line -notmatch '#\s*v?\d') {
            $failures.Add("${relative}:$($index + 1) pins '$reference' without a reviewed version comment.")
        }
    }
}

if ($failures.Count -gt 0) {
    throw ("Workflow toolchain policy failed:`n" + ($failures -join "`n"))
}

Write-Output ("Workflow toolchain policy is valid: {0} workflows and {1} composite actions." -f $workflowFiles.Count, $actionFiles.Count)
