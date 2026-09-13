[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CoverageThresholds.psm1") -Force

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-coverage-floors-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$report = Join-Path $fixtureRoot "Cobertura.xml"

function Write-Coverage {
    param(
        [string] $Line = "0.60",
        [string] $Branch = "0.40",
        [string] $WebLine = "0.80",
        [string] $WebBranch = "0.70",
        [string] $Package = "ClaimCore.Web",
        [string] $Extra = ""
    )

    $content = "<coverage line-rate=`"$Line`" branch-rate=`"$Branch`"><packages><package name=`"$Package`" line-rate=`"$WebLine`" branch-rate=`"$WebBranch`"/>$Extra</packages></coverage>"
    [IO.File]::WriteAllText($report, $content, [Text.UTF8Encoding]::new($false))
}

function Assert-Rejected {
    param([string] $Label)

    try {
        Test-ClaimCoreCoverageFloors $report | Out-Null
        throw "Coverage floor negative control '$Label' was accepted."
    }
    catch {
        if ($_.Exception.Message -eq "Coverage floor negative control '$Label' was accepted.") {
            throw
        }
    }
}

try {
    Write-Coverage
    $boundary = Test-ClaimCoreCoverageFloors $report
    if ($boundary.LineRate -ne 0.60 -or $boundary.BranchRate -ne 0.40 -or $boundary.WebPackages -ne 1) {
        throw "Exact coverage-floor boundary was not accepted."
    }

    $standardDoctype = '<!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">'
    $withDoctype = $standardDoctype + [Environment]::NewLine + [IO.File]::ReadAllText($report)
    [IO.File]::WriteAllText($report, $withDoctype, [Text.UTF8Encoding]::new($false))
    Test-ClaimCoreCoverageFloors $report | Out-Null

    Write-Coverage -Line "0.5999"
    Assert-Rejected "repository lines below 60%"
    Write-Coverage -Branch "0.3999"
    Assert-Rejected "repository branches below 40%"
    Write-Coverage -WebLine "0.7999"
    Assert-Rejected "Web lines below 80%"
    Write-Coverage -WebBranch "0.6999"
    Assert-Rejected "Web branches below 70%"
    Write-Coverage -Package "ClaimCore.Domain"
    Assert-Rejected "missing Web package"
    Write-Coverage -Extra '<package name="ClaimCore.Web.Routes" line-rate="0.7999" branch-rate="0.70"/>'
    Assert-Rejected "second Web package below floor"
    Write-Coverage -Line "NaN"
    Assert-Rejected "nonfinite repository rate"
    Write-Coverage -WebBranch "1.01"
    Assert-Rejected "out-of-range Web rate"

    [IO.File]::WriteAllText(
        $report,
        '<!DOCTYPE coverage [<!ENTITY value "0.80">]><coverage line-rate="0.60" branch-rate="0.40"><packages><package name="ClaimCore.Web" line-rate="&value;" branch-rate="0.70"/></packages></coverage>',
        [Text.UTF8Encoding]::new($false)
    )
    Assert-Rejected "DTD entity is never resolved"

    Write-Host "Merged coverage floor negative controls passed."
}
finally {
    [IO.Directory]::Delete($fixtureRoot, $true)
}
