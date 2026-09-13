[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot ".."),
    [string] $GitleaksPath = "gitleaks"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$gitRedirectVariables = @(
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_CEILING_DIRECTORIES",
    "GIT_COMMON_DIR",
    "GIT_CONFIG_COUNT",
    "GIT_CONFIG_PARAMETERS",
    "GIT_DIR",
    "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    "GIT_INDEX_FILE",
    "GIT_NAMESPACE",
    "GIT_OBJECT_DIRECTORY",
    "GIT_WORK_TREE"
)

function Invoke-ChildProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string[]] $RemoveEnvironment = @(),
        [int] $TimeoutSeconds = 300
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true

    foreach ($argument in $Arguments) {
        [void]$start.ArgumentList.Add($argument)
    }
    foreach ($name in $RemoveEnvironment) {
        [void]$start.Environment.Remove($name)
    }
    if ($FileName -eq "git") {
        $start.Environment["GIT_OPTIONAL_LOCKS"] = "0"
        $start.Environment["GIT_TERMINAL_PROMPT"] = "0"
    }

    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start

    try {
        if (-not $child.Start()) {
            throw "A required child process did not start."
        }
        $standardOutput = $child.StandardOutput.ReadToEndAsync()
        $standardError = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
            $child.Kill($true)
            $child.WaitForExit()
            throw "A required child process timed out."
        }
        return [pscustomobject]@{
            ExitCode = $child.ExitCode
            StandardOutput = $standardOutput.GetAwaiter().GetResult()
            StandardError = $standardError.GetAwaiter().GetResult()
        }
    }
    finally {
        $child.Dispose()
    }
}

function Invoke-SafeGit {
    param([string[]] $Arguments)

    return Invoke-ChildProcess -FileName "git" -Arguments $Arguments -WorkingDirectory $repoRoot `
        -RemoveEnvironment $gitRedirectVariables -TimeoutSeconds 60
}

function Get-NulPaths {
    param([string] $Raw)

    if ($Raw.Length -gt 0 -and $Raw[$Raw.Length - 1] -ne [char]0) {
        throw "Git returned a non-terminated source inventory."
    }
    $parts = $Raw.Split([char]0)
    $count = [Math]::Max(0, $parts.Length - 1)
    $paths = @($parts | Select-Object -First $count)
    if ($paths | Where-Object { [string]::IsNullOrEmpty($_) }) {
        throw "Git returned an empty source path."
    }
    return $paths
}

function Get-Inventory {
    param([string] $TemporaryGitDirectory)

    $marker = Join-Path $repoRoot ".git"
    $probe = Invoke-SafeGit @("rev-parse", "--is-inside-work-tree")
    $isWorktree = $probe.ExitCode -eq 0 -and $probe.StandardOutput.Trim() -eq "true"

    if (-not $isWorktree -and (Test-Path -LiteralPath $marker)) {
        throw "The repository contains an invalid or inaccessible Git worktree marker."
    }
    if ($isWorktree) {
        $inventory = Invoke-SafeGit @(
            "ls-files", "--cached", "--others", "--exclude-per-directory=.gitignore", "-z", "--", "."
        )
    }
    else {
        $initialized = Invoke-SafeGit @("init", "--bare", "--quiet", $TemporaryGitDirectory)
        if ($initialized.ExitCode -ne 0) {
            throw "The temporary source inventory could not be initialized."
        }
        $inventory = Invoke-SafeGit @(
            "--git-dir", $TemporaryGitDirectory,
            "--work-tree", $repoRoot,
            "ls-files", "--others", "--exclude-per-directory=.gitignore", "-z", "--", "."
        )
    }
    if ($inventory.ExitCode -ne 0) {
        throw "The source inventory could not be enumerated."
    }
    return Get-NulPaths $inventory.StandardOutput
}

function Resolve-SafeSourceFile {
    param([string] $Relative)

    if ([string]::IsNullOrWhiteSpace($Relative) -or
        [IO.Path]::IsPathFullyQualified($Relative) -or
        $Relative.Contains("\") -or
        $Relative.Contains([char]0)) {
        throw "The source inventory contains an unsafe path."
    }
    $segments = @($Relative.Split("/"))
    if ($segments | Where-Object { $_ -in "", ".", ".." }) {
        throw "The source inventory contains an unsafe path segment."
    }

    $current = $repoRoot
    foreach ($segment in $segments) {
        $matches = @(
            [IO.Directory]::EnumerateFileSystemEntries($current) |
                Where-Object { [string]::Equals([IO.Path]::GetFileName($_), $segment, [StringComparison]::Ordinal) }
        )
        if ($matches.Count -ne 1) {
            throw "A source path is missing or has different casing."
        }
        $current = $matches[0]
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source inventory paths may not contain symbolic links or junctions."
        }
    }

    $full = [IO.Path]::GetFullPath($current)
    $prefix = $repoRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, $comparison) -or
        -not [IO.File]::Exists($full) -or
        [IO.Directory]::Exists($full)) {
        throw "A source inventory entry is outside the repository or is not a regular file."
    }
    return $full
}

function Copy-Inventory {
    param([string[]] $RelativePaths, [string] $SnapshotRoot)

    $exact = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $portable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $RelativePaths) {
        if (-not $exact.Add($relative) -or -not $portable.Add($relative)) {
            throw "The source inventory contains a duplicate or cross-platform path collision."
        }
        if ($relative -eq ".gitleaks.toml") {
            throw "Repository-local Gitleaks configuration is forbidden by the source-scan policy."
        }
        $source = Resolve-SafeSourceFile $relative
        $destination = Join-Path $SnapshotRoot $relative.Replace("/", [IO.Path]::DirectorySeparatorChar)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $false)
    }
}

if (-not [IO.Directory]::Exists($repoRoot)) {
    throw "The repository root does not exist."
}
if (([IO.File]::GetAttributes($repoRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The repository root may not be a symbolic link or junction."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-source-scan-" + [Guid]::NewGuid().ToString("N"))
$snapshotRoot = Join-Path $temporaryRoot "source"
$temporaryGit = Join-Path $temporaryRoot "inventory.git"
$emptyIgnore = Join-Path $temporaryRoot "empty.gitleaksignore"
$scanExit = 70

try {
    [IO.Directory]::CreateDirectory($snapshotRoot) | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $temporaryRoot,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        )
    }
    [IO.File]::WriteAllText($emptyIgnore, "", [Text.UTF8Encoding]::new($false))
    $inventory = @(Get-Inventory $temporaryGit)
    if ($inventory.Count -eq 0) {
        throw "The source inventory is empty."
    }
    Copy-Inventory $inventory $snapshotRoot

    $gitleaks = Invoke-ChildProcess -FileName $GitleaksPath -WorkingDirectory $repoRoot `
        -RemoveEnvironment @("GITLEAKS_CONFIG", "GITLEAKS_CONFIG_TOML") `
        -Arguments @(
            "dir", "--redact", "--no-banner", "--no-color", "--exit-code", "1",
            "--ignore-gitleaks-allow", "--gitleaks-ignore-path", $emptyIgnore, $snapshotRoot
        )
    [Console]::Out.Write($gitleaks.StandardOutput)
    [Console]::Error.Write($gitleaks.StandardError)
    $scanExit = $gitleaks.ExitCode
    if ($scanExit -eq 0) {
        Write-Host "Source secret scan passed for $($inventory.Count) repository files."
    }
}
finally {
    if ([IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}

exit $scanExit
