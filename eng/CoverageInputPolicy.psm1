Set-StrictMode -Version Latest

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

Export-ModuleMember -Function "Resolve-ClaimCoreCoverageInputs"
