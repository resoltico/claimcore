[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InputRoot,
    [Parameter(Mandatory)][string] $OutputRoot,
    [string] $RunId,
    [int] $Attempt = 1,
    [string] $SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$resolve = {
    param([string] $Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}
$input = & $resolve $InputRoot
$output = & $resolve $OutputRoot
$artifactPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $output.StartsWith($artifactPrefix, [StringComparison]::Ordinal)) {
    throw "Merged coverage output must be a new directory under artifacts/."
}
if (Test-Path -LiteralPath $output) {
    throw "The merged coverage output directory must start absent."
}
if ($Attempt -lt 1 -or ([string]::IsNullOrWhiteSpace($RunId) -eq $false -and $RunId.Length -gt 100)) {
    throw "Coverage stage identity or attempt is invalid."
}

Import-Module (Join-Path $PSScriptRoot "CoverageInputPolicy.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "CoverageThresholds.psm1") -Force

$started = (Get-Date).ToUniversalTime().ToString("O")
$status = 1
$failure = $null
$manifestExit = 0
[IO.Directory]::CreateDirectory($output) | Out-Null

try {
    $reports = @(Resolve-ClaimCoreCoverageInputs $input)
    $reportArgument = "-reports:" + ($reports -join ";")
    & dotnet tool run reportgenerator -- $reportArgument "-targetdir:$output" `
        "-reporttypes:Cobertura;MarkdownSummaryGithub" "-title:ClaimCore coverage"
    if ($LASTEXITCODE -ne 0) { throw "The pinned report generator failed." }

    $assessed = Test-ClaimCoreCoverageFloors (Join-Path $output "Cobertura.xml")
    if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
        $summary = Join-Path $output "SummaryGithub.md"
        [IO.File]::AppendAllText([IO.Path]::GetFullPath($SummaryPath), [IO.File]::ReadAllText($summary))
    }

    Write-Host "Merged production coverage passed: line $([math]::Round($assessed.LineRate * 100, 2))%, branch $([math]::Round($assessed.BranchRate * 100, 2))%, Web packages $($assessed.WebPackages)."
    $status = 0
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($RunId)) {
        $finished = (Get-Date).ToUniversalTime().ToString("O")
        $outcome = if ($status -eq 0) { "success" } else { "failure" }
        $docs = Join-Path $repoRoot "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
        & dotnet $docs stage-manifest coverage $RunId "$Attempt" $outcome $started $finished $output
        $manifestExit = $LASTEXITCODE
    }
}

if ($null -ne $failure) { throw "Merged coverage failed: $failure" }
if ($manifestExit -ne 0) { throw "Coverage stage evidence could not be recorded." }
if ($status -ne 0) { throw "Merged coverage did not pass." }
