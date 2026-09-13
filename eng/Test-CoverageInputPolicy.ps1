[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CoverageInputPolicy.psm1") -Force

$root = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-coverage-policy-" + [Guid]::NewGuid().ToString("N"))

function Write-Report {
    param([string] $Relative, [string] $Content = "synthetic coverage fixture")

    $path = Join-Path $root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Content)
    return $path
}

function Assert-Rejected {
    param([string] $Label)

    try {
        Resolve-ClaimCoreCoverageInputs $root | Out-Null
        throw "Coverage negative control '$Label' was accepted."
    }
    catch {
        if ($_.Exception.Message -eq "Coverage negative control '$Label' was accepted.") {
            throw
        }
    }
}

try {
    $webClass = '<classes><class name="ClaimCore.Web.Program"><lines><line branch="True" condition-coverage="50% (1/2)"/></lines></class></classes>'
    $browserCoverage = '<coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="0.5">' + $webClass + '</package></packages></coverage>'
    $browserPath = "browser/chromium.coverage.cobertura.e2e.xml"
    Write-Report "unit/unit.coverage.cobertura.202609090000001.xml" | Out-Null
    Write-Report "web/web.coverage.cobertura.202609090000002.xml" | Out-Null
    Write-Report "integration/integration.coverage.cobertura.202609090000003.xml" | Out-Null
    foreach ($engine in @("chromium", "firefox", "webkit")) {
        Write-Report "browser/$engine.coverage.cobertura.e2e.xml" $browserCoverage | Out-Null
    }

    $resolved = @(Resolve-ClaimCoreCoverageInputs $root)
    if ($resolved.Count -ne 6) { throw "The positive coverage fixture did not resolve six reports." }

    $extra = Write-Report "browser/extra.coverage.cobertura.injected.xml"
    Assert-Rejected "unexpected report"
    [IO.File]::Delete($extra)

    $duplicate = Write-Report "unit/unit.coverage.cobertura.202609090000004.xml"
    Assert-Rejected "duplicate role"
    [IO.File]::Delete($duplicate)

    Write-Report $browserPath '<coverage branches-covered="0" branches-valid="0"><packages/></coverage>' | Out-Null
    Assert-Rejected "empty browser coverage"
    Write-Report $browserPath ('<coverage branches-covered="0" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="0">' + $webClass + '</package></packages></coverage>') | Out-Null
    Assert-Rejected "uncovered browser branches"
    Write-Report $browserPath '<coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Domain" branch-rate="0.5"><classes><class name="ClaimCore.Domain.Claim"/></classes></package></packages></coverage>' | Out-Null
    Assert-Rejected "missing Web production package"
    Write-Report $browserPath ('<coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="0">' + $webClass + '</package></packages></coverage>') | Out-Null
    Assert-Rejected "unmeasured Web production branches"
    $unmeasuredWeb = '<classes><class name="ClaimCore.Web.Program"><lines><line branch="True" condition-coverage="0% (0/2)"/></lines></class></classes>'
    Write-Report $browserPath ('<coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="0.5">' + $unmeasuredWeb + '</package></packages></coverage>') | Out-Null
    Assert-Rejected "other-package branch counters cannot stand in for Web evidence"
    Write-Report $browserPath ('<!DOCTYPE coverage [<!ENTITY rate "0.5">]><coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="&rate;">' + $webClass + '</package></packages></coverage>') | Out-Null
    Assert-Rejected "DTD entity is never resolved"
    Write-Report $browserPath $browserCoverage | Out-Null

    Write-Host "Coverage input policy negative controls passed."
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        [IO.Directory]::Delete($root, $true)
    }
}
