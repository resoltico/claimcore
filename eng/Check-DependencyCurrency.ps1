[CmdletBinding()]
param([string] $RegistryPath = "")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$arguments = @((Join-Path $PSScriptRoot "ci/dependency-check.mjs"), "health")
if (-not [string]::IsNullOrWhiteSpace($RegistryPath)) { $arguments += $RegistryPath }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw "Dependency health needs attention; inspect artifacts/dependency-health/report.json." }
