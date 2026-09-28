[CmdletBinding()]
param(
    [string] $Assembly = "",
    [string] $RunRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository "artifacts"))
$verificationBase = Join-Path $artifacts "local-verification"
$runId = "local-" + [Guid]::NewGuid().ToString("N")
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = "artifacts/local-verification/$runId"
}
$resolved = if ([IO.Path]::IsPathRooted($RunRoot)) {
    [IO.Path]::GetFullPath($RunRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $RunRoot))
}
$parent = [IO.Path]::GetDirectoryName($resolved)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not [string]::Equals($parent, $verificationBase, $comparison) -or
    (Test-Path -LiteralPath $resolved) -or
    $null -ne (Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue)) {
    throw "The local verification root must be a new direct child of artifacts/local-verification/."
}
foreach ($existing in @($artifacts, $verificationBase)) {
    $item = Get-Item -LiteralPath $existing -Force -ErrorAction SilentlyContinue
    if ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A local verification parent is a symbolic link or junction."
        }
    }
}

$suites = @(
    [PSCustomObject]@{ Assembly = "ClaimCore.Tests"; Expected = 334; Configuration = "Release"; Coverage = "unit"; Timeout = "25m"; Stage = "unit" }
    [PSCustomObject]@{ Assembly = "ClaimCore.WebTests"; Expected = 122; Configuration = "Release"; Coverage = "web"; Timeout = "25m"; Stage = "web" }
    [PSCustomObject]@{ Assembly = "ClaimCore.DocsTests"; Expected = 74; Configuration = "Release"; Coverage = ""; Timeout = "25m"; Stage = "docs" }
    [PSCustomObject]@{ Assembly = "ClaimCore.FuzzQualificationTests"; Expected = 5; Configuration = "Release"; Coverage = ""; Timeout = "25m"; Stage = "fuzz" }
    [PSCustomObject]@{ Assembly = "ClaimCore.ArchitectureTests"; Expected = 88; Configuration = "Debug"; Coverage = ""; Timeout = "25m"; Stage = "architecture" }
    [PSCustomObject]@{ Assembly = "ClaimCore.IntegrationTests"; Expected = 347; Configuration = "Release"; Coverage = "integration"; Timeout = "90m"; Stage = "integration" }
    [PSCustomObject]@{ Assembly = "ClaimCore.WitnessTests"; Expected = 21; Configuration = "Release"; Coverage = ""; Timeout = "90m"; Stage = "witness-qualification" }
    [PSCustomObject]@{ Assembly = "ClaimCore.RecoveryQualificationTests"; Expected = 19; Configuration = "Release"; Coverage = ""; Timeout = "90m"; Stage = "recovery-qualification" }
    [PSCustomObject]@{ Assembly = "ClaimCore.ConcurrencyQualificationTests"; Expected = 5; Configuration = "Release"; Coverage = ""; Timeout = "90m"; Stage = "concurrency-qualification" }
    [PSCustomObject]@{ Assembly = "ClaimCore.MigrationQualificationTests"; Expected = 15; Configuration = "Release"; Coverage = ""; Timeout = "90m"; Stage = "fresh-baseline-qualification" }
    [PSCustomObject]@{ Assembly = "ClaimCore.BackupQualificationTests"; Expected = 2; Configuration = "Release"; Coverage = ""; Timeout = "90m"; Stage = "backup-qualification" }
)
$selected = if ([string]::IsNullOrWhiteSpace($Assembly)) {
    $suites
} else {
    @($suites | Where-Object { $_.Assembly -ceq $Assembly })
}
if ($selected.Count -eq 0) { throw "The requested .NET test assembly is not registered." }

Set-Location $repository
dotnet restore ClaimCore.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Locked .NET restore failed." }
dotnet build ClaimCore.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
[IO.Directory]::CreateDirectory($resolved) | Out-Null
$docs = Join-Path $repository "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
$sourceFingerprint = (& dotnet $docs source-fingerprint)
if ($LASTEXITCODE -ne 0 -or $sourceFingerprint -notmatch '^[0-9a-f]{64}:[0-9a-f]{64}$') {
    throw "The local source fingerprint could not be established."
}
$platform = if ($IsWindows) { "windows" } elseif ($IsMacOS) { "macos" } elseif ($IsLinux) { "linux" } else { throw "Unsupported test platform." }

foreach ($suite in $selected) {
    $project = Join-Path $repository "tests/$($suite.Assembly)/$($suite.Assembly).fsproj"
    if ($suite.Configuration -eq "Debug") {
        dotnet build $project --configuration Debug --no-restore -p:Optimize=false
        if ($LASTEXITCODE -ne 0) { throw "The Debug architecture build failed." }
    }
    $results = if ($suite.Coverage -ne "") {
        Join-Path $resolved "coverage-input/$($suite.Coverage)"
    } else {
        Join-Path $resolved "test-results/$($suite.Assembly)"
    }
    if (Test-Path -LiteralPath $results) { throw "A local test result directory already exists." }
    $arguments = @(
        "test", "--project", $project, "--configuration", $suite.Configuration,
        "--no-build", "--no-restore", "--max-parallel-test-modules", "1",
        "--results-directory=$results", "--minimum-expected-tests=$($suite.Expected)",
        "--zero-tests-policy=strict", "--timeout=$($suite.Timeout)", "--",
        "--settings=$repository/eng/expecto.runsettings", "--report-trx",
        "--report-trx-filename=$($suite.Assembly).trx"
    )
    if ($suite.Coverage -ne "") {
        $arguments += @("--coverlet", "--coverlet-file-prefix=$($suite.Coverage)", "--coverlet-output-format=cobertura")
    }
    if ($suite.Configuration -eq "Debug") {
        $env:CLAIMCORE_ARCHITECTURE_REPORT = Join-Path $results "architecture-report.json"
    }
    $started = (Get-Date).ToUniversalTime().ToString("O")
    try {
        & dotnet @arguments
        $testExit = $LASTEXITCODE
    } finally {
        if ($suite.Configuration -eq "Debug") { Remove-Item Env:CLAIMCORE_ARCHITECTURE_REPORT -ErrorAction SilentlyContinue }
    }
    $finished = (Get-Date).ToUniversalTime().ToString("O")
    if ($testExit -ne 0) { throw "$($suite.Assembly) did not complete every registered test." }
    $trx = Join-Path $results "$($suite.Assembly).trx"
    $relative = [IO.Path]::GetRelativePath($repository, $trx).Replace([IO.Path]::DirectorySeparatorChar, '/')
    dotnet $docs verify-test-report $suite.Assembly $relative
    if ($LASTEXITCODE -ne 0) { throw "$($suite.Assembly) TRX identity verification failed." }
    if ($suite.Stage -in @("unit", "web", "docs", "fuzz", "architecture")) {
        $stage = "$($suite.Stage)-$platform"
    } elseif ($platform -eq "linux") {
        $stage = $suite.Stage
    } else {
        $stage = ""
    }
    if ($stage -ne "") {
        dotnet $docs stage-manifest $stage $runId 1 success $started $finished ([IO.Path]::GetRelativePath($repository, $results).Replace([IO.Path]::DirectorySeparatorChar, '/'))
        if ($LASTEXITCODE -ne 0) { throw "$($suite.Assembly) local stage manifest failed." }
    }
}

$finishedFingerprint = (& dotnet $docs source-fingerprint)
if ($LASTEXITCODE -ne 0 -or $finishedFingerprint -cne $sourceFingerprint) {
    throw "Source changed while the local .NET suites were running."
}
if ($selected.Count -eq $suites.Count) {
    [IO.File]::WriteAllText(
        (Join-Path $resolved "complete-source-fingerprint.txt"),
        $sourceFingerprint + [Environment]::NewLine
    )
}

Write-Host "Local .NET verification passed for $($selected.Count) registered suite(s)."
Write-Host "Retained synthetic reports: $resolved"
if ($selected.Count -ne $suites.Count) {
    Write-Host "This selected-suite run is not complete local verification."
}
