[CmdletBinding()]
param(
    [string] $DocsAssembly = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($DocsAssembly)) {
    $DocsAssembly = Join-Path $repoRoot "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
}

if (-not [IO.File]::Exists($DocsAssembly)) {
    throw "ClaimCore.Docs must be built before convergence assurance can run."
}

$arguments = @(
    $DocsAssembly,
    "convergence",
    "check",
    "eng/test-baseline-v0.1.json",
    "eng/test-lineage.json",
    "eng/assurance-matrix.json"
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Convergence assurance failed."
}
