[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $repository
$docs = Join-Path $repository "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
if (-not [IO.File]::Exists($docs)) { throw "Build ClaimCore.Docs before checking documentation." }

function Read-SourceState {
    $fingerprint = (& dotnet $docs source-fingerprint)
    if ($LASTEXITCODE -ne 0 -or $fingerprint -notmatch '^[0-9a-f]{64}:[0-9a-f]{64}$') {
        throw "Could not establish the current tracked and untracked source fingerprint."
    }
    return $fingerprint
}

$before = Read-SourceState
dotnet $docs check
if ($LASTEXITCODE -ne 0) { throw "Documentation check failed." }

dotnet $docs write
if ($LASTEXITCODE -ne 0) { throw "The first documentation write failed." }
if ((Read-SourceState) -cne $before) {
    throw "The first documentation write changed source relative to its starting state."
}

dotnet $docs write
if ($LASTEXITCODE -ne 0) { throw "The second documentation write failed." }
if ((Read-SourceState) -cne $before) {
    throw "The second documentation write changed source relative to its starting state."
}

Write-Host "Documentation check and both byte-idle writes passed without changing existing user edits."
