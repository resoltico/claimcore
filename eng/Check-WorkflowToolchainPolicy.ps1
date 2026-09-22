[CmdletBinding()]
param([string] $Root = "")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot ".." }
& node (Join-Path $PSScriptRoot "ci/check-workflows.mjs") ([IO.Path]::GetFullPath($Root))
if ($LASTEXITCODE -ne 0) { throw "Structural workflow governance failed." }
