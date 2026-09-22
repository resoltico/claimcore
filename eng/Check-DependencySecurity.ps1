[CmdletBinding()]
param([string] $RegistryPath = "")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$arguments = @((Join-Path $PSScriptRoot "ci/dependency-check.mjs"), "security")
if (-not [string]::IsNullOrWhiteSpace($RegistryPath)) { $arguments += $RegistryPath }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw "Dependency security or approved-hold policy failed; inspect the safe dependency report." }
