[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$docsAssembly = Join-Path $repoRoot "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
$checker = Join-Path $PSScriptRoot "Check-ConvergenceAssurance.ps1"
$nonce = [Guid]::NewGuid().ToString("N")
$baselineProbe = "eng/ConvergenceBaselineProbe-$nonce.json"
$lineageProbe = "eng/ConvergenceLineageProbe-$nonce.json"
$matrixProbe = "eng/ConvergenceMatrixProbe-$nonce.json"
$baselineProbePath = Join-Path $repoRoot $baselineProbe
$lineageProbePath = Join-Path $repoRoot $lineageProbe
$matrixProbePath = Join-Path $repoRoot $matrixProbe

function Write-Json {
    param([string] $Path, [object] $Value)

    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Invoke-Probe {
    param([string] $Expected)

    $output = @(
        & dotnet $docsAssembly convergence check $baselineProbe $lineageProbe $matrixProbe 2>&1
    )
    $text = $output -join [Environment]::NewLine
    if ($LASTEXITCODE -eq 0 -or -not $text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "Convergence assurance negative control '$Expected' did not fail for the intended reason."
    }
}

try {
    [IO.File]::Copy((Join-Path $repoRoot "eng/test-baseline-v0.1.json"), $baselineProbePath)
    [IO.File]::Copy((Join-Path $repoRoot "eng/test-lineage.json"), $lineageProbePath)
    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath)

    $baseline = Get-Content -Raw -LiteralPath $baselineProbePath | ConvertFrom-Json
    $baseline.sources[0].sourceSha256 = "a" * 64
    Write-Json $baselineProbePath $baseline
    Invoke-Probe "immutable v0.1 baseline bytes"

    [IO.File]::Copy((Join-Path $repoRoot "eng/test-baseline-v0.1.json"), $baselineProbePath, $true)
    $lineage = Get-Content -Raw -LiteralPath $lineageProbePath | ConvertFrom-Json
    $lineage.entries = @($lineage.entries | Select-Object -Skip 1)
    Write-Json $lineageProbePath $lineage
    Invoke-Probe "Every baseline test identity"

    [IO.File]::Copy((Join-Path $repoRoot "eng/test-lineage.json"), $lineageProbePath, $true)
    $lineage = Get-Content -Raw -LiteralPath $lineageProbePath | ConvertFrom-Json
    $lineage.entries[0].status = "replaced"
    $lineage.entries[0].replacementIds = @("not-a-live-test")
    Write-Json $lineageProbePath $lineage
    Invoke-Probe "replacement identity must exist"

    [IO.File]::Copy((Join-Path $repoRoot "eng/test-lineage.json"), $lineageProbePath, $true)
    $lineage = Get-Content -Raw -LiteralPath $lineageProbePath | ConvertFrom-Json
    $replacement = @($lineage.entries | Where-Object { $_.status -eq "replaced" } | Select-Object -First 1)
    if ($replacement.Count -ne 1) { throw "Expected a replaced lineage entry." }
    $replacement[0].reason = "The legacy identity was removed by the hard cutover; its interface obligation is exercised by the declared live typed replacement without any subject-specific assertion."
    Write-Json $lineageProbePath $lineage
    Invoke-Probe "Replaced lineage needs a subject-specific assertion reason"

    [IO.File]::Copy((Join-Path $repoRoot "eng/test-lineage.json"), $lineageProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    # Preserve the contract's ordinal order so this probe fails on its missing identity,
    # not on culture-sensitive ordering of otherwise valid architecture test names.
    [string[]] $testIds = @($matrix.entries[0].tests) + @("not-a-live-test")
    [Array]::Sort($testIds, [StringComparer]::Ordinal)
    $matrix.entries[0].tests = $testIds
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "matrix references a test absent"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $matrix.entries = @($matrix.entries | Where-Object { $_.id -ne "endpoint-cli:case.get" })
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Every generated CLI-v3 and Web-v2 endpoint"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $matrix.entries = @($matrix.entries | Where-Object { $_.id -ne "branch-cli:invalid-unicode" })
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Every registered CLI-v3 and Web-v2 protocol branch"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $endpoint = @($matrix.entries | Where-Object { $_.id -eq "endpoint-cli:case.get" })
    if ($endpoint.Count -ne 1) { throw "Expected one CLI case.get outcome subject." }
    $endpoint[0].outcomeTags = @($endpoint[0].outcomeTags | Select-Object -Skip 1)
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Every endpoint needs its exact generated outcome-tag inventory"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $matrix.entries = @($matrix.entries | Where-Object { $_.id -ne "assurance-cancellation:resolve-after-attempt" })
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Every registered architecture, core, recovery, cancellation, migration, and GUI assurance subject"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $matrix.entries = @($matrix.entries | Where-Object { $_.id -ne "assurance-architecture:product-ownership" })
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Every registered architecture, core, recovery, cancellation, migration, and GUI assurance subject"

    [IO.File]::Copy((Join-Path $repoRoot "eng/assurance-matrix.json"), $matrixProbePath, $true)
    $matrix = Get-Content -Raw -LiteralPath $matrixProbePath | ConvertFrom-Json
    $endpoint = @($matrix.entries | Where-Object { $_.id -eq "endpoint-cli:case.get" })
    if ($endpoint.Count -ne 1) { throw "Expected one CLI case.get evidence subject." }
    $endpoint[0].tests = @($endpoint[0].tests | Where-Object { $_ -notlike "dotnet:ClaimCore.AcceptanceTests::*" })
    Write-Json $matrixProbePath $matrix
    Invoke-Probe "Endpoint assurance must retain its exact published or TestServer runtime"
}
finally {
    @($baselineProbePath, $lineageProbePath, $matrixProbePath) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        ForEach-Object { [IO.File]::Delete($_) }
}

& $checker -DocsAssembly $docsAssembly
if ($LASTEXITCODE -ne 0) {
    throw "Convergence assurance positive control failed after temporary fixtures were removed."
}

Write-Host "Convergence assurance policy negative controls passed."
