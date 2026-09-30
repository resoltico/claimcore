[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^ClaimCore\.[A-Za-z]*Tests$')]
    [string] $Assembly,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inventoryPath = Join-Path $repository "eng/ClaimCore.Docs/test-inventory/$Assembly.json"
$binary = Join-Path $repository "artifacts/bin/$Assembly/$($Configuration.ToLowerInvariant())/$Assembly.dll"
if (-not [IO.File]::Exists($inventoryPath) -or -not [IO.File]::Exists($binary)) {
    throw 'Registered test discovery inputs are absent.'
}

$inventory = Get-Content -Raw -LiteralPath $inventoryPath | ConvertFrom-Json -AsHashtable
if ($inventory.schemaVersion -ne 1 -or $inventory.assembly -cne $Assembly -or
    $inventory.tests -isnot [array] -or $inventory.tests.Count -eq 0) {
    throw 'Registered test inventory is invalid.'
}

$listed = @(& dotnet $binary --list-tests json)
if ($LASTEXITCODE -ne 0 -or $listed.Count -eq 0) {
    throw 'Built test discovery did not complete.'
}
$discovery = ($listed -join "`n") | ConvertFrom-Json -AsHashtable
if ($discovery.schemaVersion -ne 1 -or $discovery.tests -isnot [array]) {
    throw 'Built test discovery has an invalid shape.'
}

$expected = [string[]] @($inventory.tests)
$actual = [string[]] @($discovery.tests | ForEach-Object { $_.displayName })
$ordinal = [StringComparer]::Ordinal
[Array]::Sort($expected, $ordinal)
[Array]::Sort($actual, $ordinal)
if ($expected.Length -ne $actual.Length -or $actual.Length -eq 0) {
    throw 'Built test names differ from the registered inventory.'
}
for ($index = 0; $index -lt $expected.Length; $index++) {
    if ([string]::IsNullOrWhiteSpace($actual[$index]) -or
        -not $ordinal.Equals($expected[$index], $actual[$index]) -or
        ($index -gt 0 -and $ordinal.Equals($actual[$index - 1], $actual[$index]))) {
        throw 'Built test names differ from the registered inventory.'
    }
}

$partitionRegistry = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'test-partitions.json') | ConvertFrom-Json -AsHashtable
$partitioned = @($partitionRegistry.assemblies | Where-Object { $_.assembly -ceq $Assembly })
if ($partitioned.Count -gt 1) { throw 'Test partitions are registered twice for one assembly.' }
if ($partitioned.Count -eq 1) {
    $selector = [string] $partitioned[0].selector
    $union = [Collections.Generic.HashSet[string]]::new($ordinal)
    foreach ($partition in $partitioned[0].partitions) {
        $previous = [Environment]::GetEnvironmentVariable($selector)
        [Environment]::SetEnvironmentVariable($selector, [string] $partition.id)
        try { $partitionListed = @(& dotnet $binary --list-tests json) } finally { [Environment]::SetEnvironmentVariable($selector, $previous) }
        if ($LASTEXITCODE -ne 0 -or $partitionListed.Count -eq 0) { throw "Partition '$($partition.id)' discovery did not complete." }
        $names = @((($partitionListed -join "`n") | ConvertFrom-Json -AsHashtable).tests | ForEach-Object { $_.displayName })
        if ($names.Count -ne [int] $partition.tests) { throw "Partition '$($partition.id)' discovers $($names.Count) tests; $($partition.tests) are registered." }
        foreach ($name in $names) {
            if (-not $union.Add($name)) { throw "Test '$name' belongs to more than one partition." }
        }
    }
    if ($union.Count -ne $expected.Length -or -not $union.SetEquals([string[]] $expected)) {
        throw 'Registered partitions do not cover exactly the registered test inventory.'
    }
    Write-Host "Built $Assembly partitions cover exactly its $($expected.Length) registered tests."
}

Write-Host "Built $Assembly discovery matches $($actual.Length) registered tests."
