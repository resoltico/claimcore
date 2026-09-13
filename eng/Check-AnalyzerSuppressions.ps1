[CmdletBinding()]
param([string] $RegistryPath = "")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.FSharp.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.Frontend.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.Common.psm1") -Force

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($RegistryPath)) {
    $RegistryPath = Join-Path $repoRoot "analyzer-suppressions.json"
}

function Get-GeneratedExclusions {
    param([Text.Json.JsonElement] $Root)

    $generatedExclusions = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Root.GetProperty("generatedExclusions").EnumerateArray()) {
        $generatedPath = Get-RequiredText $entry "path"
        $generator = Get-RequiredText $entry "generator"
        Assert-Governance $entry $generatedPath
        if ($generator.Length -lt 8) {
            throw "$generatedPath must name its generator specifically."
        }
        $allowedGeneratedPath = '^(?:artifacts/obj/|src/ClaimCore\.Web/wwwroot/|web/(?:artifacts|coverage|dist|node_modules|playwright-report|test-results)/)$'
        if ($generatedPath -notmatch $allowedGeneratedPath) {
            throw "$generatedPath is not an exact recognized generated-output path."
        }
        if ($generatedExclusions.Contains($generatedPath)) {
            throw "Duplicate generated exclusion: $generatedPath"
        }
        $generatedExclusions.Add($generatedPath)
    }
    return $generatedExclusions
}

function Get-SuppressionRegistry {
    param([Text.Json.JsonElement] $Root, [string] $RepoRoot)

    $registry = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $nonSuppressibleRulePattern = '(?i)(^|[-_/.:])(max[-_]?lines|file[-_]?size|function[-_]?size|complexity|focused|skip(?:ped)?[-_]?test|test[-_]?filter|contract[-_]?drift|security[-_]?boundary)([-_/.:]|$)'
    foreach ($entry in $Root.GetProperty("suppressions").EnumerateArray()) {
        $file = Get-RequiredText $entry "file"
        $rule = Get-RequiredText $entry "rule"
        $scope = Get-RequiredText $entry "scope"
        Assert-Governance $entry "$file/$rule"
        if ($file -match '[*?]' -or $file.EndsWith('/') -or [IO.Path]::IsPathRooted($file)) {
            throw "$file/$rule must name one exact repository-relative file."
        }
        if ($rule -match '[*?]' -or $rule -match $nonSuppressibleRulePattern) {
            throw "$file/$rule attempts to suppress a non-suppressible or non-exact rule."
        }
        if ($scope -notmatch '^(file|project|line:[1-9][0-9]*)$') {
            throw "$file/$rule has an invalid scope."
        }
        if (-not [IO.File]::Exists((Join-Path $RepoRoot $file))) {
            throw "$file/$rule points to a file that does not exist."
        }
        $key = "$file|$rule|$scope"
        if (-not $seen.Add($key)) {
            throw "Duplicate suppression registry entry: $key"
        }
        $registry.Add([pscustomobject]@{ File = $file; Rule = $rule; Scope = $scope; Key = $key })
    }
    return $registry
}

function Assert-RegistryMatchesOccurrences {
    param(
        [Collections.Generic.List[object]] $Registry,
        [Collections.Generic.List[object]] $Occurrences
    )

    foreach ($occurrence in $Occurrences) {
        $key = "$($occurrence.File)|$($occurrence.Rule)|$($occurrence.Scope)"
        if (-not ($Registry | Where-Object Key -CEQ $key)) {
            throw "$($occurrence.File):$($occurrence.Line) has unregistered suppression $($occurrence.Rule) ($($occurrence.Scope))."
        }
    }
    foreach ($entry in $Registry) {
        if (-not ($Occurrences | Where-Object { "$($_.File)|$($_.Rule)|$($_.Scope)" -ceq $entry.Key })) {
            throw "Stale suppression registry entry: $($entry.Key)"
        }
    }
}

$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($RegistryPath))
try {
    $root = $document.RootElement
    if ($root.GetProperty("version").GetInt32() -ne 1) {
        throw "Unsupported analyzer suppression registry version."
    }

    $generatedExclusions = Get-GeneratedExclusions $root
    $registry = Get-SuppressionRegistry $root $repoRoot
    $occurrences = [Collections.Generic.List[object]]::new()
    $sourceFiles = Get-PolicySourceFiles $repoRoot $generatedExclusions

    Assert-PhysicalFileSizeLimits $sourceFiles $repoRoot
    Find-FSharpSuppressions $sourceFiles $repoRoot $occurrences
    Assert-NoFocusedOrSkippedFSharpTests $sourceFiles $repoRoot
    Assert-NoSensitiveTestDiagnostics $sourceFiles $repoRoot
    Assert-NoRequiredCiTestFilters $repoRoot
    Find-BrowserSuppressions $sourceFiles $repoRoot $occurrences
    Find-ProjectWarningSuppressions $repoRoot $generatedExclusions $occurrences
    Assert-FSharpLintPolicy $repoRoot $generatedExclusions
    Find-ConfigurationSuppressions $repoRoot $generatedExclusions $occurrences
    Assert-RegistryMatchesOccurrences $registry $occurrences

    Write-Host "Analyzer suppression registry is valid: $($registry.Count) registered, $($occurrences.Count) active."
}
finally {
    $document.Dispose()
}
