<#
.SYNOPSIS
Runs PSScriptAnalyzer with every rule over the tracked PowerShell sources and fails on any finding.
.DESCRIPTION
The settings file is config/PSScriptAnalyzerSettings.psd1. Its exclusions are registered in
config/lint-exceptions.json, so a rule cannot be silenced anywhere else.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$module = Get-Module -ListAvailable -Name PSScriptAnalyzer | Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $module -or $module.Version -ne [Version]'1.25.0') {
    throw 'PSScriptAnalyzer 1.25.0 is required.'
}
Import-Module -Name $module.Path -Force

$settings = Join-Path $root 'config/PSScriptAnalyzerSettings.psd1'
$files = @(git -C $root ls-files '*.ps1' '*.psm1' '*.psd1')
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) {
    throw 'The tracked PowerShell sources could not be listed.'
}

$findings = @(foreach ($file in $files) {
        Invoke-ScriptAnalyzer -Path (Join-Path $root $file) -Settings $settings
    })
foreach ($finding in $findings) {
    $relative = [IO.Path]::GetRelativePath($root, $finding.ScriptPath)
    Write-Information ('{0}:{1} {2} {3}' -f $relative, $finding.Line, $finding.RuleName, $finding.Message) -InformationAction Continue
}
if ($findings.Count -gt 0) {
    throw ('PSScriptAnalyzer reported {0} finding(s).' -f $findings.Count)
}
Write-Information ('PSScriptAnalyzer checked {0} file(s) with every rule.' -f $files.Count) -InformationAction Continue
