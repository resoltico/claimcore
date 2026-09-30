[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,
    [Parameter(Mandatory = $true)]
    [string] $Attempt,
    # How many test processes may run at once. Each owns its own PostgreSQL containers, so the limit
    # is about the machine, not about isolation.
    [int] $MaxParallel = 0,
    # Where per-stage results go; CI uses the default.
    [string] $ResultsRoot = "artifacts/test-results",
    # Stage ids to run; empty means every registered stage.
    [string[]] $StageIds = @(),
    # Run and merge without recording stage manifests, for platforms with no registered Linux stages.
    [switch] $NoEvidence
)

# Runs every PostgreSQL-backed test assembly through native MTP, concurrently, and records one stage
# manifest per registered stage. CI and the local verification run this same script.
#
# The integration suite runs as the partitions registered in eng/test-partitions.json, each in its own
# process against its own clusters. Their reports are merged into the one report the stage registers,
# and the merged report is verified against the compiled inventory exactly as a single run would be.
#
# Not run in this script and not registered anywhere else: Debug, unit and Web suites (see the
# workflows and eng/Run-LocalDotnetVerification.ps1).

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $repository
$docs = Join-Path $repository "artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
if (-not (Test-Path -LiteralPath $docs)) { throw "Build the Release evidence executable first." }

if ($MaxParallel -le 0) {
    $configured = $env:CLAIMCORE_PARALLEL_JOBS
    $MaxParallel = if ($configured -match '^[1-9][0-9]*$') { [int] $configured } else { 8 }
}

# Registered floors: each assembly's exact minimum test count, independent of its inventory.
$stages = @(
    @{ Stage = "integration-linux"; Assembly = "ClaimCore.IntegrationTests"; Project = "tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj"; Expected = 372; Timeout = "110m"; Coverage = $true }
    @{ Stage = "backup-qualification"; Assembly = "ClaimCore.BackupQualificationTests"; Project = "tests/ClaimCore.BackupQualificationTests/ClaimCore.BackupQualificationTests.fsproj"; Expected = 2; Timeout = "90m"; Coverage = $false }
    @{ Stage = "recovery-qualification"; Assembly = "ClaimCore.RecoveryQualificationTests"; Project = "tests/ClaimCore.RecoveryQualificationTests/ClaimCore.RecoveryQualificationTests.fsproj"; Expected = 19; Timeout = "90m"; Coverage = $false }
    @{ Stage = "witness-qualification"; Assembly = "ClaimCore.WitnessTests"; Project = "tests/ClaimCore.WitnessTests/ClaimCore.WitnessTests.fsproj"; Expected = 21; Timeout = "90m"; Coverage = $false }
    @{ Stage = "fresh-baseline-qualification"; Assembly = "ClaimCore.MigrationQualificationTests"; Project = "tests/ClaimCore.MigrationQualificationTests/ClaimCore.MigrationQualificationTests.fsproj"; Expected = 15; Timeout = "90m"; Coverage = $false }
    @{ Stage = "concurrency-qualification"; Assembly = "ClaimCore.ConcurrencyQualificationTests"; Project = "tests/ClaimCore.ConcurrencyQualificationTests/ClaimCore.ConcurrencyQualificationTests.fsproj"; Expected = 5; Timeout = "90m"; Coverage = $false }
)

$resultsBase = if ([IO.Path]::IsPathRooted($ResultsRoot)) { [IO.Path]::GetFullPath($ResultsRoot) } else { [IO.Path]::GetFullPath((Join-Path $repository $ResultsRoot)) }
if ($StageIds.Count -gt 0) {
    $unknown = @($StageIds | Where-Object { $_ -notin @($stages | ForEach-Object { $_.Stage }) })
    if ($unknown.Count -gt 0) { throw "Unregistered PostgreSQL stage(s): $($unknown -join ', ')." }
    $stages = @($stages | Where-Object { $_.Stage -in $StageIds })
}

$registry = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "test-partitions.json") | ConvertFrom-Json -AsHashtable

function Copy-Tree {
    param([string] $Source, [string] $Destination)

    foreach ($file in [IO.Directory]::EnumerateFiles($Source, "*", [IO.SearchOption]::AllDirectories)) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file, $target)
    }
}

function New-TestProcess {
    param($Job)

    $common = @(
        "--results-directory=$($Job.Results)", "--minimum-expected-tests=$($Job.Expected)",
        "--zero-tests-policy=strict", "--timeout=$($Job.Timeout)"
    )
    $settings = @("--settings=$repository/eng/expecto.runsettings", "--report-trx", "--report-trx-filename=$($Job.TrxName)")
    if ($Job.CoveragePrefix) {
        # Coverlet rewrites the test's assemblies on disk while it measures. Concurrent measured
        # processes therefore each run a private copy of the test's output directory, never a shared one.
        $source = Join-Path $repository "artifacts/bin/$($Job.Assembly)/release"
        # Inside the repository, because tests that start product processes locate the repository root
        # by walking up from their own directory.
        $Job.PrivateBin = Join-Path $repository ("artifacts/measured-bin/" + [Guid]::NewGuid().ToString("N"))
        Copy-Tree $source $Job.PrivateBin
        $arguments = @((Join-Path $Job.PrivateBin "$($Job.Assembly).dll")) + $common + $settings +
            @("--coverlet", "--coverlet-file-prefix=$($Job.CoveragePrefix)", "--coverlet-output-format=cobertura")
    } else {
        $arguments = @(
            "test", "--project", $Job.Project, "--configuration", "Release", "--no-build", "--no-restore",
            "--max-parallel-test-modules", "1"
        ) + $common + @("--") + $settings
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $repository
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    if ($Job.Selector) { $startInfo.Environment[$Job.Selector] = $Job.Partition }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    return $process
}

# One job per test process; the integration stage contributes one job per registered partition.
$jobs = [Collections.Generic.List[hashtable]]::new()
foreach ($stage in $stages) {
    $partitioned = @($registry.assemblies | Where-Object { $_.assembly -ceq $stage.Assembly })
    if ($partitioned.Count -gt 1) { throw "Partitions are registered twice for $($stage.Assembly)." }
    if ($partitioned.Count -eq 1) {
        $counted = 0
        foreach ($partition in $partitioned[0].partitions) {
            $counted += [int] $partition.tests
            $jobs.Add(@{
                Stage = $stage.Stage; Assembly = $stage.Assembly; Project = $stage.Project
                Expected = [int] $partition.tests; Timeout = $stage.Timeout
                Selector = [string] $partitioned[0].selector; Partition = [string] $partition.id
                Results = Join-Path $resultsBase "$($stage.Stage)-partitions/$($partition.id)"
                TrxName = "$($stage.Assembly).$($partition.id).trx"
                CoveragePrefix = if ($stage.Coverage) { "integration-$($partition.id)" } else { $null }
            })
        }
        if ($counted -ne $stage.Expected) { throw "Registered partitions of $($stage.Assembly) do not sum to its registered count." }
    } else {
        $jobs.Add(@{
            Stage = $stage.Stage; Assembly = $stage.Assembly; Project = $stage.Project
            Expected = $stage.Expected; Timeout = $stage.Timeout; Selector = $null; Partition = $null
            Results = Join-Path $resultsBase $stage.Stage
            TrxName = "$($stage.Assembly).trx"
            CoveragePrefix = $null
        })
    }
}

foreach ($job in $jobs) {
    if (Test-Path -LiteralPath $job.Results) { throw "The test result directory must start absent." }
}

# Start in registered order (longest first) and keep at most MaxParallel processes running.
$pending = [Collections.Generic.Queue[hashtable]]::new($jobs)
$running = [Collections.Generic.List[hashtable]]::new()
$finished = [Collections.Generic.List[hashtable]]::new()

function Complete-Job {
    param($Job)

    $Job.Process.WaitForExit()
    [Console]::Out.WriteLine("::group::$($Job.Stage)$(if ($Job.Partition) { " [$($Job.Partition)]" })")
    [Console]::Out.Write($Job.StandardOutput.GetAwaiter().GetResult())
    [Console]::Error.Write($Job.StandardError.GetAwaiter().GetResult())
    [Console]::Out.WriteLine("::endgroup::")
    $Job.ExitCode = $Job.Process.ExitCode
    $Job.Finished = $Job.Process.ExitTime.ToUniversalTime()
    $Job.Process.Dispose()
    if ($Job.ContainsKey("PrivateBin") -and (Test-Path -LiteralPath $Job.PrivateBin)) {
        Remove-Item -LiteralPath $Job.PrivateBin -Recurse -Force
    }
}

while ($pending.Count -gt 0 -or $running.Count -gt 0) {
    while ($pending.Count -gt 0 -and $running.Count -lt $MaxParallel) {
        $job = $pending.Dequeue()
        $process = New-TestProcess $job
        $job.Started = (Get-Date).ToUniversalTime()
        if (-not $process.Start()) { throw "The $($job.Stage) test process did not start." }
        $job.Process = $process
        $job.StandardOutput = $process.StandardOutput.ReadToEndAsync()
        $job.StandardError = $process.StandardError.ReadToEndAsync()
        $running.Add($job)
    }
    $done = @($running | Where-Object { $_.Process.HasExited })
    if ($done.Count -eq 0) { Start-Sleep -Milliseconds 500; continue }
    foreach ($job in $done) {
        Complete-Job $job
        [void] $running.Remove($job)
        $finished.Add($job)
    }
}

# One manifest per stage, in the shape every single-process stage has always had.
$overall = 0
foreach ($stage in $stages) {
    $own = @($finished | Where-Object { $_.Stage -ceq $stage.Stage })
    $results = Join-Path $resultsBase $stage.Stage
    $succeeded = @($own | Where-Object { $_.ExitCode -eq 0 }).Count -eq $own.Count

    if ($own[0].Partition) {
        [IO.Directory]::CreateDirectory($results) | Out-Null
        if ($succeeded) {
            $inputs = @($own | ForEach-Object {
                [IO.Path]::GetRelativePath($repository, (Join-Path $_.Results $_.TrxName)).Replace([IO.Path]::DirectorySeparatorChar, '/')
            })
            $merged = [IO.Path]::GetRelativePath($repository, (Join-Path $results "$($stage.Assembly).trx")).Replace([IO.Path]::DirectorySeparatorChar, '/')
            & dotnet $docs merge-test-reports $stage.Assembly $merged @inputs
            if ($LASTEXITCODE -ne 0) { $succeeded = $false }
            foreach ($job in $own) {
                Get-ChildItem -LiteralPath $job.Results -File -Filter "*.coverage.cobertura.*.xml" |
                    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $results }
            }
        }
    } elseif (-not (Test-Path -LiteralPath $results)) {
        [IO.Directory]::CreateDirectory($results) | Out-Null
    }

    $started = ($own | ForEach-Object { $_.Started } | Sort-Object | Select-Object -First 1).ToString("O")
    $ended = ($own | ForEach-Object { $_.Finished } | Sort-Object -Descending | Select-Object -First 1).ToString("O")
    $outcome = if ($succeeded) { "success" } else { "failure" }
    $recorded = $true
    if (-not $NoEvidence) {
        & dotnet $docs stage-manifest $stage.Stage $RunId $Attempt $outcome $started $ended $results
        $recorded = $LASTEXITCODE -eq 0
    }
    if (-not $succeeded -or -not $recorded) { $overall = 1 }
    $seconds = [int]([DateTimeOffset]::Parse($ended) - [DateTimeOffset]::Parse($started)).TotalSeconds
    Write-Host "Stage $($stage.Stage): $outcome in ${seconds}s ($($own.Count) process(es))."
}

exit $overall
