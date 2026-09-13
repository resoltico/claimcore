Set-StrictMode -Version Latest

function Read-CoberturaRate {
    param(
        [Parameter(Mandatory)][object] $Node,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][double] $Minimum,
        [Parameter(Mandatory)][string] $Subject
    )

    $raw = $Node.GetAttribute($Name)
    $rate = 0.0
    $valid = [double]::TryParse(
        $raw,
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref] $rate
    )
    if (-not $valid -or -not [double]::IsFinite($rate) -or $rate -lt 0.0 -or $rate -gt 1.0) {
        throw "$Subject has an invalid $Name value."
    }
    if ($rate -lt $Minimum) {
        throw "$Subject $Name is below the required $($Minimum * 100)% floor."
    }
    return $rate
}

function Test-ClaimCoreCoverageFloors {
    param([Parameter(Mandatory)][string] $CoberturaPath)

    $settings = [Xml.XmlReaderSettings]::new()
    # ReportGenerator emits Cobertura's external doctype; ignore it without resolving entities.
    $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($CoberturaPath), $settings)

    try {
        $report = [Xml.XmlDocument]::new()
        $report.XmlResolver = $null
        $report.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    if ($null -eq $report.DocumentElement -or $report.DocumentElement.Name -ne "coverage") {
        throw "Merged coverage must be a Cobertura coverage document."
    }

    $line = Read-CoberturaRate $report.DocumentElement "line-rate" 0.60 "Merged production coverage"
    $branch = Read-CoberturaRate $report.DocumentElement "branch-rate" 0.40 "Merged production coverage"
    $webPackages = @($report.SelectNodes('/coverage/packages/package[starts-with(@name, "ClaimCore.Web")]'))
    if ($webPackages.Count -eq 0) {
        throw "Merged coverage contains no ClaimCore.Web production package."
    }

    foreach ($package in $webPackages) {
        [void] (Read-CoberturaRate $package "line-rate" 0.80 "ClaimCore.Web package")
        [void] (Read-CoberturaRate $package "branch-rate" 0.70 "ClaimCore.Web package")
    }

    return [PSCustomObject]@{
        LineRate = $line
        BranchRate = $branch
        WebPackages = $webPackages.Count
    }
}

Export-ModuleMember -Function "Test-ClaimCoreCoverageFloors"
