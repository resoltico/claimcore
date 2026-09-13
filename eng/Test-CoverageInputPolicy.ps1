[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CoverageInputPolicy.psm1") -Force

$root = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-coverage-policy-" + [Guid]::NewGuid().ToString("N"))

function Write-Report {
    param([string] $Relative)

    $path = Join-Path $root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, "synthetic coverage fixture")
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
    Write-Report "unit/unit.coverage.cobertura.202609090000001.xml" | Out-Null
    Write-Report "web/web.coverage.cobertura.202609090000002.xml" | Out-Null
    Write-Report "integration/integration.coverage.cobertura.202609090000003.xml" | Out-Null
    foreach ($engine in @("chromium", "firefox", "webkit")) {
        Write-Report "browser/$engine.coverage.cobertura.e2e.xml" | Out-Null
    }

    $resolved = @(Resolve-ClaimCoreCoverageInputs $root)
    if ($resolved.Count -ne 6) { throw "The positive coverage fixture did not resolve six reports." }

    $extra = Write-Report "browser/extra.coverage.cobertura.injected.xml"
    Assert-Rejected "unexpected report"
    [IO.File]::Delete($extra)

    $duplicate = Write-Report "unit/unit.coverage.cobertura.202609090000004.xml"
    Assert-Rejected "duplicate role"
    [IO.File]::Delete($duplicate)

    Write-Host "Coverage input policy negative controls passed."
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        [IO.Directory]::Delete($root, $true)
    }
}
