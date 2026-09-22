[CmdletBinding()]
param(
    [Parameter(Position = -2147483648)]
    [string] $GitleaksArchivePath = $env:CLAIMCORE_SCANNER_ARCHIVE,
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $ArtifactPaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Fixed release assets and SHA-256 digests checked against both the official
# v8.30.1 checksum asset and GitHub's release-asset digests. Never trust a
# caller-supplied executable or a checksum downloaded alongside the archive.
$releaseBase = "https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/"
$assets = @{
    "Linux-X64" = @("gitleaks_8.30.1_linux_x64.tar.gz", "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb")
    "Linux-Arm64" = @("gitleaks_8.30.1_linux_arm64.tar.gz", "e4a487ee7ccd7d3a7f7ec08657610aa3606637dab924210b3aee62570fb4b080")
    "OSX-X64" = @("gitleaks_8.30.1_darwin_x64.tar.gz", "dfe101a4db2255fc85120ac7f3d25e4342c3c20cf749f2c20a18081af1952709")
    "OSX-Arm64" = @("gitleaks_8.30.1_darwin_arm64.tar.gz", "b40ab0ae55c505963e365f271a8d3846efbc170aa17f2607f13df610a9aeb6a5")
    "Windows-X64" = @("gitleaks_8.30.1_windows_x64.zip", "d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e")
    "Windows-Arm64" = @("gitleaks_8.30.1_windows_arm64.zip", "b95f5e4f5c425cedca7ee203d9afd29597e692c4924a12ed42f970537c72cc0f")
}

function Assert-ArtifactTree {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains([char]0) -or
        $Path.Contains("*") -or $Path.Contains("?")) {
        throw "Invalid artifact path."
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($full) -and -not [IO.Directory]::Exists($full)) {
        throw "Missing artifact path."
    }

    $ancestor = $full
    while ($ancestor) {
        if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Artifact path contains a link."
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }

    # Gitleaks scans ordinary files only. Reject links, special files, and
    # target-local configuration/ignore files instead of silently omitting them.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($full)
    $regularFiles = 0
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Artifact tree contains a link."
        }
        $leaf = [IO.Path]::GetFileName($entry)
        if ($leaf -eq ".gitleaks.toml" -or $leaf -eq ".gitleaksignore") {
            throw "Artifact tree contains a scanner override."
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($entry)) {
                $pending.Push($child)
            }
        }
        elseif (($attributes -band [IO.FileAttributes]::Device) -ne 0) {
            throw "Artifact tree contains a special file."
        }
        else {
            $regularFiles++
        }
    }
    if ($regularFiles -eq 0) {
        throw "Artifact tree is empty."
    }
    return $full
}

function Get-ArtifactFingerprint {
    param([string] $Path)

    # Revalidate the tree on both sides of the scan. Hash file names and
    # contents so a concurrent writer cannot silently change uploaded bytes
    # during this helper's run. A later CI step still needs its own scan.
    [void](Assert-ArtifactTree $Path)
    $pending = [Collections.Generic.Stack[string]]::new()
    $files = [Collections.Generic.List[string]]::new()
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::Device) -ne 0) {
            throw "Artifact tree changed during fingerprinting."
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($entry)) {
                $pending.Push($child)
            }
        }
        else {
            $files.Add($entry)
        }
    }
    $files.Sort([StringComparer]::Ordinal)
    $fingerprint = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256
    )
    try {
        foreach ($file in $files) {
            $fileHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            $record = [Text.Encoding]::UTF8.GetBytes($file + [char]0 + $fileHash + [char]0)
            $fingerprint.AppendData($record)
        }
        return [Convert]::ToHexString($fingerprint.GetHashAndReset())
    }
    finally { $fingerprint.Dispose() }
}

function Invoke-QuietProcess {
    param([string] $FileName, [string[]] $Arguments, [string] $WorkingDirectory)

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$start.ArgumentList.Add($argument)
    }
    [void]$start.Environment.Remove("GITLEAKS_CONFIG")
    [void]$start.Environment.Remove("GITLEAKS_CONFIG_TOML")
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw "Scanner did not start."
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Scanner timed out."
        }
        # Drain but never display or persist scanner output: even redacted
        # findings can include data-bearing source lines and file names.
        [void]$stdout.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Install-VerifiedGitleaks {
    param([string] $TemporaryRoot, [string[]] $Asset)

    $archive = Join-Path $TemporaryRoot $Asset[0]
    if ($GitleaksArchivePath) {
        if (-not [IO.File]::Exists($GitleaksArchivePath)) {
            throw "Verified scanner archive is missing."
        }
        [IO.File]::Copy([IO.Path]::GetFullPath($GitleaksArchivePath), $archive, $false)
    }
    else {
        Invoke-WebRequest -Uri ($releaseBase + $Asset[0]) -OutFile $archive -MaximumRedirection 5 | Out-Null
    }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $Asset[1], [StringComparison]::OrdinalIgnoreCase)) {
        throw "Scanner archive failed integrity verification."
    }

    $binary = Join-Path $TemporaryRoot $(if ($IsWindows) { "gitleaks.exe" } else { "gitleaks" })
    if ($IsWindows) {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entries = @($zip.Entries | Where-Object { $_.FullName -eq "gitleaks.exe" })
            if ($entries.Count -ne 1) {
                throw "Scanner archive does not contain one executable."
            }
            $source = $entries[0].Open()
            $destination = [IO.File]::Create($binary)
            try { $source.CopyTo($destination) }
            finally { $destination.Dispose(); $source.Dispose() }
        }
        finally { $zip.Dispose() }
    }
    else {
        if ((Invoke-QuietProcess -FileName "tar" -Arguments @("-xzf", $archive, "-C", $TemporaryRoot, "gitleaks") -WorkingDirectory $TemporaryRoot) -ne 0) {
            throw "Scanner archive extraction failed."
        }
        [IO.File]::SetUnixFileMode(
            $binary,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        )
    }
    if (-not [IO.File]::Exists($binary)) {
        throw "Verified scanner executable is missing."
    }
    return $binary
}

$exitCode = 1
$temporaryRoot = $null
try {
    if ($null -eq $ArtifactPaths -or $ArtifactPaths.Count -eq 0) {
        throw "No artifact paths supplied."
    }
    $targets = @($ArtifactPaths | ForEach-Object { Assert-ArtifactTree $_ })
    $fingerprints = @($targets | ForEach-Object { Get-ArtifactFingerprint $_ })
    $platform = if ($IsWindows) { "Windows" } elseif ($IsMacOS) { "OSX" } elseif ($IsLinux) { "Linux" } else { "Unsupported" }
    $key = "$platform-$([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
    if (-not $assets.ContainsKey($key)) {
        throw "Unsupported scanner platform."
    }

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-artifact-scan-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode(
            $temporaryRoot,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        )
    }
    $emptyIgnore = Join-Path $temporaryRoot "empty.gitleaksignore"
    [IO.File]::WriteAllText($emptyIgnore, "", [Text.UTF8Encoding]::new($false))
    $binary = Install-VerifiedGitleaks -TemporaryRoot $temporaryRoot -Asset $assets[$key]
    $exitCode = 0
    foreach ($target in $targets) {
        $result = Invoke-QuietProcess -FileName $binary -WorkingDirectory $temporaryRoot -Arguments @(
            "dir", "--redact=100", "--no-banner", "--no-color", "--exit-code", "1",
            "--ignore-gitleaks-allow", "--gitleaks-ignore-path", $emptyIgnore,
            "--max-archive-depth", "3", "--max-decode-depth", "3", $target
        )
        if ($result -ne 0) {
            $exitCode = 1
        }
    }
    for ($index = 0; $index -lt $targets.Count; $index++) {
        if ((Get-ArtifactFingerprint $targets[$index]) -ne $fingerprints[$index]) {
            $exitCode = 1
        }
    }
    if ($exitCode -eq 0) {
        [Console]::Out.WriteLine("Artifact secret scan passed for $($targets.Count) explicit path(s).")
    }
    else {
        [Console]::Error.WriteLine("Artifact secret scan completed and refused its targets.")
    }
}
catch {
    # Do not emit exception details, target paths, tool logs, or matched bytes. The message still
    # distinguishes a scanner that could not run from a scan that completed and found something,
    # because a fail-closed gate that cannot tell an operator which one happened is not actionable.
    [Console]::Error.WriteLine("Artifact secret scan could not complete; the scanner did not run.")
    $exitCode = 1
}
finally {
    if ($temporaryRoot -and [IO.Directory]::Exists($temporaryRoot)) {
        try { [IO.Directory]::Delete($temporaryRoot, $true) }
        catch { [Console]::Error.WriteLine("Artifact scanner cleanup failed."); $exitCode = 1 }
    }
}
exit $exitCode
