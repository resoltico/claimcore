[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$tests = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "ci") -Filter '*.test.mjs' -File | Sort-Object Name | ForEach-Object FullName)
if ($tests.Count -eq 0) { throw "Workflow governance tests are missing." }
& node --test @tests
if ($LASTEXITCODE -ne 0) { throw "Workflow governance negative controls failed." }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
& node (Join-Path $repository "web/node_modules/prettier/bin/prettier.cjs") --check (Join-Path $PSScriptRoot "ci/*.mjs")
if ($LASTEXITCODE -ne 0) { throw "Governance tooling formatting failed." }
