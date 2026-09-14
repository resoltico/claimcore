[CmdletBinding()]
param([string] $RepositoryRoot = (Join-Path $PSScriptRoot ".."))

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-ignore-policy-" + [Guid]::NewGuid().ToString("N"))
$temporaryGit = Join-Path $temporaryRoot "inventory.git"
$gitRedirectVariables = @(
    "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CEILING_DIRECTORIES", "GIT_COMMON_DIR",
    "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_DIR", "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    "GIT_INDEX_FILE", "GIT_NAMESPACE", "GIT_OBJECT_DIRECTORY", "GIT_WORK_TREE"
)

$mustBeIgnored = @(
    ".local/private-state", "web/.local/browser-profile/History", ".direnv/cache", ".envrc",
    ".envrc.local", ".env", ".env.production", ".npmrc", "nested/.npmrc", ".netrc",
    "nested/_netrc",
    "private/admin.connection", "private/bootstrap-credential-current",
    "private/claimcore-recovery-operation.json", "private/web.pfx", "private/web.p12",
    "private/web.pkcs12", "private/web.pem", "private/web.key", "private/signing.jks",
    "private/signing.keystore", "private/signing.snk", ".pgpass", "nested/pgpass.conf",
    "artifacts/report.json", "artifacts/native/ClaimCore.Cli/Release/libclaimcore_hostsecurity_native.dylib",
    "artifacts/native/ClaimCore.Web/Release/libclaimcore_hostsecurity_native.so",
    "src/ClaimCore.Web/wwwroot/assets/app.js",
    "nested/node_modules/package/index.js", "nested/dist/index.js", "web/artifacts/report.json",
    "nested/.cache/state", "TestResults/result.trx", "nested/test-results/result.json",
    "nested/.vitest/results.json", "nested/playwright-report/index.html", "nested/coverage/lcov.info",
    "src/Probe/bin/output.dll", "src/Probe/obj/project.assets.json", "trace.binlog",
    "package.nupkg", "package.snupkg", "test.coverage", "test.coveragexml", "test.trx",
    "types.tsbuildinfo", "BenchmarkDotNet.Artifacts/report.html", "npm-debug.log.1",
    "yarn-debug.log", "yarn-error.log", "pnpm-debug.log", ".vs/session.json",
    ".idea/workspace.xml", ".fleet/settings.json", ".history/source.fs", ".ionide/state.json",
    ".vscode/tasks.json", "workspace.rsuser", "workspace.user", "workspace.suo",
    "workspace.userosscache", "workspace.sln.docstates", "source.fs.swp", "source.fs.swo",
    "source.fs~", "source.fs.orig", "source.fs.rej", ".DS_Store", "nested/._source",
    "Thumbs.db", "Desktop.ini", "web/.eslintcache", "web/.stylelintcache",
    "web/blob-report/report.zip", "web/playwright/.auth/session.json",
    "web/playwright/.cache/browser.json"
)

$mustRemainVisible = @(
    ".gitignore", "web/.gitignore", ".env.example", ".config/dotnet-tools.json",
    ".editorconfig", ".gitattributes", ".node-version", ".vscode/extensions.json",
    ".vscode/settings.json", "AGENTS.md", "CHANGELOG.md", "CONTRIBUTING.md", "LICENSE",
    "README.md", "ClaimCore.slnx", "Directory.Build.props", "Directory.Packages.props",
    "NuGet.Config", "analyzer-suppressions.json", "compose.yaml", "dependency-holds.json",
    "db/001_initial.sql", "db/002_request_preparations.sql", "db/003_submission_attempts.sql",
    "db/004_generalize_preparation_provenance.sql", "db/005_recovery_evidence_read_acl.sql", "db/006_operation_authority_and_business_time.sql",
    "db/migration-manifest.json",
    "docs/development.md", "eng/Check-GitIgnorePolicy.ps1",
    "src/ClaimCore.Domain/Claim.fs", "tests/ClaimCore.Tests/Suite.fs", "web/.npmrc",
    "src/ClaimCore.HostSecurity/ClaimCore.HostSecurity.fsproj",
    "src/ClaimCore.HostSecurity/AssemblyInfo.fs",
    "src/ClaimCore.HostSecurity/HostSecurity.Native.targets",
    "src/ClaimCore.HostSecurity/native/claimcore_private_openat.c",
    "src/ClaimCore.HostSecurity/packages.lock.json",
    "src/ClaimCore.HostSecurity/PrivateFileService.fs",
    "src/ClaimCore.HostSecurity/PosixPrivateFiles.fs",
    "src/ClaimCore.HostSecurity/PosixPrivateNative.fs",
    "web/.prettierignore", "web/package.json", "web/package-lock.json", "web/src/App.tsx",
    "web/src/generated/convergence/semantic-core-v1.contract.json",
    "web/src/generated/convergence/web-v2.endpoint-catalog.ts"
)

if (-not [IO.Directory]::Exists($repoRoot)) {
    throw "The repository root does not exist."
}

$savedEnvironment = @{}
foreach ($name in $gitRedirectVariables) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $temporaryRoot,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        )
    }

    & git init --bare --quiet $temporaryGit
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated Git ignore database could not be initialized."
    }

    $allProbes = @($mustBeIgnored) + @($mustRemainVisible)
    $probeInput = $allProbes -join "`n"
    $ignoredOutput = @($probeInput | & git --git-dir $temporaryGit --work-tree $repoRoot `
        -c core.quotePath=false check-ignore --no-index --stdin)
    if ($LASTEXITCODE -notin 0, 1) {
        throw "Git could not classify the ignore-policy probes."
    }
    $ignoredPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ignoredOutput | ForEach-Object { [void]$ignoredPaths.Add($_) }

    foreach ($relative in $mustBeIgnored) {
        if (-not $ignoredPaths.Contains($relative)) {
            throw "A required private or generated path is not ignored: $relative"
        }
    }

    foreach ($relative in $mustRemainVisible) {
        if (-not [IO.File]::Exists((Join-Path $repoRoot $relative))) {
            throw "A required public release input is missing: $relative"
        }
        if ($ignoredPaths.Contains($relative)) {
            throw "A required public release input is ignored: $relative"
        }
    }
}
finally {
    foreach ($name in $gitRedirectVariables) {
        if ($null -eq $savedEnvironment[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
        }
    }
    if ([IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}

Write-Host "Git ignore policy passed for $($mustBeIgnored.Count) private/generated probes and $($mustRemainVisible.Count) public release inputs."
