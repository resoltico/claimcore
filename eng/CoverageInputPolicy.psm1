Set-StrictMode -Version Latest

function Assert-ClaimCoreBrowserCoverage {
    param([Parameter(Mandatory)][string] $Path)

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path), $settings)

    try {
        $report = [Xml.XmlDocument]::new()
        $report.XmlResolver = $null
        $report.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    if ($null -eq $report.DocumentElement -or $report.DocumentElement.Name -ne "coverage") {
        throw "Browser coverage must be a Cobertura coverage document."
    }

    $covered = 0L
    $valid = 0L
    $integerStyle = [Globalization.NumberStyles]::None
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $coveredText = $report.DocumentElement.GetAttribute("branches-covered")
    $validText = $report.DocumentElement.GetAttribute("branches-valid")

    if (-not [long]::TryParse($coveredText, $integerStyle, $culture, [ref] $covered) -or
        -not [long]::TryParse($validText, $integerStyle, $culture, [ref] $valid) -or
        $covered -lt 1 -or $valid -lt $covered) {
        throw "Browser coverage must contain measured branch counters."
    }

    $webPackages = @($report.SelectNodes('/coverage/packages/package[@name="ClaimCore.Web"]'))
    if ($webPackages.Count -ne 1) {
        throw "Browser coverage must contain the ClaimCore.Web production package."
    }

    $branchRate = 0.0
    $branchText = $webPackages[0].GetAttribute("branch-rate")
    if (-not [double]::TryParse($branchText, [Globalization.NumberStyles]::Float, $culture, [ref] $branchRate) -or
        -not [double]::IsFinite($branchRate) -or $branchRate -le 0.0 -or $branchRate -gt 1.0) {
        throw "Browser coverage must measure ClaimCore.Web production branches."
    }

    $measuredWebBranches = 0L
    $webBranchLines = $webPackages[0].SelectNodes('./classes/class/lines/line[@branch="True" or @branch="true"]')
    foreach ($line in $webBranchLines) {
        $condition = [regex]::Match($line.GetAttribute("condition-coverage"), '\(([0-9]+)/([0-9]+)\)$')
        if (-not $condition.Success) {
            throw "Browser coverage has invalid ClaimCore.Web branch evidence."
        }
        $coveredHere = [long]::Parse($condition.Groups[1].Value, $culture)
        $validHere = [long]::Parse($condition.Groups[2].Value, $culture)
        if ($validHere -lt 1 -or $coveredHere -gt $validHere) {
            throw "Browser coverage has invalid ClaimCore.Web branch evidence."
        }
        $measuredWebBranches += $coveredHere
    }
    if ($measuredWebBranches -lt 1) {
        throw "Browser coverage must contain measured ClaimCore.Web branch evidence."
    }
}

function Resolve-ClaimCoreCoverageInputs {
    param([Parameter(Mandatory)][string] $InputRoot)

    $root = [IO.Path]::GetFullPath($InputRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "The coverage input root does not exist."
    }

    $reports = [Collections.Generic.List[string]]::new()
    foreach ($role in @("unit", "web", "integration")) {
        $directory = Join-Path $root $role
        $matches = @(
            Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
                Where-Object { $_.Name -match "^$role\.coverage\.cobertura\.[0-9]{15}\.xml$" }
        )
        if ($matches.Count -ne 1) {
            throw "Coverage role '$role' must provide exactly one timestamped report."
        }
        $reports.Add($matches[0].FullName)
    }

    $browserDirectory = Join-Path $root "browser"
    foreach ($engine in @("chromium", "firefox", "webkit")) {
        $path = Join-Path $browserDirectory "$engine.coverage.cobertura.e2e.xml"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Browser coverage role '$engine' is missing."
        }
        Assert-ClaimCoreBrowserCoverage $path
        $reports.Add([IO.Path]::GetFullPath($path))
    }

    $allCoverage = @(
        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object { $_.Name -match '\.coverage\.cobertura\..*\.xml$' } |
            ForEach-Object { $_.FullName }
    )
    $expected = @($reports | Sort-Object)
    $actual = @($allCoverage | Sort-Object)
    if (Compare-Object $expected $actual -CaseSensitive) {
        throw "Coverage inputs contain a missing, duplicate, misplaced, or unexpected report."
    }

    return @($reports)
}

Export-ModuleMember -Function "Resolve-ClaimCoreCoverageInputs", "Assert-ClaimCoreBrowserCoverage"
