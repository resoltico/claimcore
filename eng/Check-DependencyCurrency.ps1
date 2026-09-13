[CmdletBinding()]
param([string] $RegistryPath = "")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.Common.psm1") -Force

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($RegistryPath)) {
    $RegistryPath = Join-Path $repoRoot "dependency-holds.json"
}

function Invoke-JsonProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [int[]] $AllowedExitCodes
    )

    $start = [Diagnostics.ProcessStartInfo]::new($FileName)
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "A dependency metadata process did not start." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr))
    if ($process.ExitCode -notin $AllowedExitCodes) {
        throw "A dependency metadata process failed."
    }
    if ([string]::IsNullOrWhiteSpace($stdout.Result)) { return @{} }
    return $stdout.Result | ConvertFrom-Json -Depth 20
}

function Get-ExactToolVersion {
    param([string] $FileName, [string[]] $Arguments)

    $start = [Diagnostics.ProcessStartInfo]::new($FileName)
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "A toolchain version process did not start." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr))
    if ($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($stdout.Result)) {
        throw "A toolchain version process failed."
    }
    return $stdout.Result.Trim()
}

function ConvertTo-SemanticVersion {
    param([string] $Value, [string] $Package)

    try { return [System.Management.Automation.SemanticVersion]::new($Value) }
    catch { throw "npm returned a non-semantic version for '$Package'." }
}

function Get-NpmCurrentLineLatest {
    param([string] $Package, [System.Management.Automation.SemanticVersion] $Current, [string] $WorkingDirectory)

    $published = Invoke-JsonProcess npm @("view", "$Package@$($Current.Major)", "version", "--json") $WorkingDirectory @(0)
    $versions = @($published) | ForEach-Object { ConvertTo-SemanticVersion $_ $Package }
    if ($versions.Count -eq 0) { throw "npm returned no published versions for '$Package'." }
    return $versions | Sort-Object | Select-Object -Last 1
}

function Get-PackageEntries {
    param([object] $Document, [string[]] $Collections)

    $entries = [Collections.Generic.List[object]]::new()
    foreach ($project in @($Document.projects)) {
        $frameworks = $project.PSObject.Properties["frameworks"]
        if ($null -eq $frameworks) { continue }
        foreach ($framework in @($frameworks.Value)) {
            foreach ($collection in $Collections) {
                $property = $framework.PSObject.Properties[$collection]
                if ($null -ne $property) {
                    foreach ($package in @($property.Value)) { $entries.Add($package) }
                }
            }
        }
    }
    return @($entries)
}

function Get-Holds {
    param([string] $Path)

    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $root = $document.RootElement
        if ($root.GetProperty("version").GetInt32() -ne 1) { throw "Unsupported dependency-hold registry version." }
        $holds = [Collections.Generic.List[object]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $root.GetProperty("holds").EnumerateArray()) {
            $ecosystem = Get-RequiredText $entry "ecosystem"
            $package = Get-RequiredText $entry "package"
            $current = Get-RequiredText $entry "current"
            $latest = Get-RequiredText $entry "latest"
            Assert-Governance $entry "$ecosystem/$package"
            if ($ecosystem -notin "nuget", "npm" -or $current -eq $latest) {
                throw "$ecosystem/$package is not a valid exact dependency hold."
            }
            $key = "$ecosystem|$package|$current|$latest"
            if (-not $seen.Add($key)) { throw "Duplicate dependency hold: $key" }
            $holds.Add([pscustomobject]@{ Key = $key; Ecosystem = $ecosystem })
        }
        return @($holds)
    }
    finally { $document.Dispose() }
}

function Assert-ExactHolds {
    param([object[]] $Registered, [string[]] $Actual)

    $expected = @($Registered | ForEach-Object Key | Sort-Object)
    $observed = @($Actual | Sort-Object -Unique)
    $difference = @(Compare-Object $expected $observed -CaseSensitive)
    if ($difference.Count -ne 0) {
        $keys = $difference | ForEach-Object InputObject | Sort-Object -Unique
        throw "Dependency holds are stale, missing, or no longer exact: $($keys -join ', ')."
    }
}

$webRoot = Join-Path $repoRoot "web"
$packageManifest = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Join-Path $webRoot "package.json")))
try {
    $expectedNode = [IO.File]::ReadAllText((Join-Path $repoRoot ".node-version")).Trim()
    $expectedNpm = $packageManifest.RootElement.GetProperty("engines").GetProperty("npm").GetString()
    $packageManager = $packageManifest.RootElement.GetProperty("packageManager").GetString()
    if ([string]::IsNullOrWhiteSpace($expectedNode) -or [string]::IsNullOrWhiteSpace($expectedNpm)) {
        throw "The exact Node and npm versions are missing."
    }
    if ($packageManager -cne "npm@$expectedNpm") {
        throw "packageManager and engines.npm do not select the same exact npm release."
    }
    $actualNode = (Get-ExactToolVersion node @("--version")).TrimStart([char] "v")
    $actualNpm = Get-ExactToolVersion npm @("--version")
    if ($actualNode -cne $expectedNode -or $actualNpm -cne $expectedNpm) {
        throw "Dependency metadata must be checked with Node $expectedNode and npm $expectedNpm."
    }
}
finally { $packageManifest.Dispose() }

$solution = Join-Path $repoRoot "ClaimCore.slnx"
$common = @("package", "list", "--project", $solution, "--format", "json", "--output-version", "1", "--no-restore")
$direct = Invoke-JsonProcess dotnet ($common + "--outdated") $repoRoot @(0)
if (@(Get-PackageEntries $direct @("topLevelPackages")).Count -ne 0) {
    throw "A direct NuGet dependency is not current."
}

foreach ($mode in @("--deprecated", "--vulnerable")) {
    $audit = Invoke-JsonProcess dotnet ($common + @($mode, "--include-transitive")) $repoRoot @(0)
    if (@(Get-PackageEntries $audit @("topLevelPackages", "transitivePackages")).Count -ne 0) {
        throw "NuGet dependency audit '$mode' found a package."
    }
}

$transitive = Invoke-JsonProcess dotnet ($common + @("--outdated", "--include-transitive")) $repoRoot @(0)
$nuget = Get-PackageEntries $transitive @("transitivePackages") | ForEach-Object {
    "nuget|$($_.id)|$($_.resolvedVersion)|$($_.latestVersion)"
}
$npmDocument = Invoke-JsonProcess npm @("outdated", "--json") $webRoot @(0, 1)
$npm = $npmDocument.PSObject.Properties | ForEach-Object {
    $currentProperty = $_.Value.PSObject.Properties["current"]
    $latestProperty = $_.Value.PSObject.Properties["latest"]
    if ($null -eq $currentProperty -or [string]::IsNullOrWhiteSpace($currentProperty.Value)) {
        throw "The locked npm graph is not installed; run 'npm --prefix web ci' before the dependency check."
    }
    if ($null -eq $latestProperty -or [string]::IsNullOrWhiteSpace($latestProperty.Value)) {
        throw "npm did not return a latest version for '$($_.Name)'."
    }
    $current = ConvertTo-SemanticVersion $currentProperty.Value $_.Name
    $latest = ConvertTo-SemanticVersion $latestProperty.Value $_.Name
    if ($latest -gt $current) {
        "npm|$($_.Name)|$($currentProperty.Value)|$($latestProperty.Value)"
    }
    elseif ($current -gt $latest) {
        $currentLineLatest = Get-NpmCurrentLineLatest $_.Name $current $webRoot
        if ($currentLineLatest -gt $current) {
            "npm|$($_.Name)|$($currentProperty.Value)|$currentLineLatest"
        }
    }
}
$holds = Get-Holds $RegistryPath
Assert-ExactHolds $holds (@($nuget) + @($npm))
Write-Host "Dependency currency is valid: no direct updates and $($holds.Count) exact upstream holds."
