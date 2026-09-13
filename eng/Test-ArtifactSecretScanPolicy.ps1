[CmdletBinding()]
param([string] $GitleaksArchivePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scanner = Join-Path $PSScriptRoot "Scan-ArtifactSecrets.ps1"
$pwsh = (Get-Process -Id $PID).Path
$testRoot = Join-Path (Join-Path $PSScriptRoot "../artifacts") (
    "claimcore-artifact-scan-policy-" + [Guid]::NewGuid().ToString("N")
)
$probeRoot = Join-Path $testRoot "fixture"
$platform = if ($IsWindows) { "Windows" } elseif ($IsMacOS) { "OSX" } elseif ($IsLinux) { "Linux" } else { "Unsupported" }
$key = "$platform-$([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
$assets = @{
    "Linux-X64" = "gitleaks_8.30.1_linux_x64.tar.gz"
    "Linux-Arm64" = "gitleaks_8.30.1_linux_arm64.tar.gz"
    "OSX-X64" = "gitleaks_8.30.1_darwin_x64.tar.gz"
    "OSX-Arm64" = "gitleaks_8.30.1_darwin_arm64.tar.gz"
    "Windows-X64" = "gitleaks_8.30.1_windows_x64.zip"
    "Windows-Arm64" = "gitleaks_8.30.1_windows_arm64.zip"
}
if (-not $assets.ContainsKey($key)) {
    throw "Unsupported artifact-scan policy test platform."
}
$archivePath = if ($GitleaksArchivePath) {
    [IO.Path]::GetFullPath($GitleaksArchivePath)
} else {
    Join-Path $testRoot $assets[$key]
}
$canary =
    "aws_access_key_id = " + "AK" + "IA7JQ4N2P6R8T0V3X5" + [Environment]::NewLine +
    "aws_secret_access_key = " + "7Yk3pQ9v" + "L2mN8cR4" + "tW6xZ1aB" + "5dF0hJ7s" + "K9uE3iO6"

function Invoke-Scanner {
    param([string[]] $Paths)

    $output = @(& $pwsh -NoProfile -File $scanner -GitleaksArchivePath $archivePath @Paths 2>&1)
    $combined = $output -join "`n"
    if ($combined.Contains("AK" + "IA7JQ4N2P6R8T0V3X5") -or
        $combined.Contains("7Yk3pQ9v" + "L2mN8cR4" + "tW6xZ1aB" + "5dF0hJ7s" + "K9uE3iO6")) {
        throw "Artifact scanner emitted synthetic secret bytes."
    }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $combined }
}

try {
    [IO.Directory]::CreateDirectory($probeRoot) | Out-Null
    if (-not $GitleaksArchivePath) {
        Invoke-WebRequest -Uri (
            "https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/" + $assets[$key]
        ) -OutFile $archivePath | Out-Null
    }
    $safe = Join-Path $probeRoot "safe.txt"
    [IO.File]::WriteAllText($safe, "synthetic artifact without a credential`n")

    if ((Invoke-Scanner @()).ExitCode -eq 0) {
        throw "An absent artifact path passed the scan."
    }
    $missing = Join-Path $probeRoot "missing.txt"
    if ((Invoke-Scanner @($safe, $missing)).ExitCode -eq 0) {
        throw "A missing artifact path was silently skipped."
    }
    if ((Invoke-Scanner @($safe)).ExitCode -ne 0) {
        throw "A benign artifact failed the scan."
    }
    $discarded = @(& $pwsh -NoProfile -File $scanner -GitleaksArchivePath $safe $safe 2>&1)
    if ($LASTEXITCODE -eq 0) {
        throw "An unverified scanner archive was accepted."
    }

    $ordinary = Join-Path $probeRoot "ordinary.txt"
    [IO.File]::WriteAllText($ordinary, $canary)
    if ((Invoke-Scanner @($safe, $ordinary)).ExitCode -eq 0) {
        throw "A synthetic credential in a later artifact path passed the scan."
    }
    [IO.File]::Delete($ordinary)

    $inlineAllow = Join-Path $probeRoot "inline-allow.txt"
    $annotated = ($canary -replace [Environment]::NewLine, " # gitleaks:allow`n") + " # gitleaks:allow"
    [IO.File]::WriteAllText($inlineAllow, $annotated)
    if ((Invoke-Scanner @($inlineAllow)).ExitCode -eq 0) {
        throw "An inline Gitleaks allow marker bypassed the artifact scan."
    }
    [IO.File]::Delete($inlineAllow)

    $archiveSource = Join-Path $probeRoot "archive-source"
    [IO.Directory]::CreateDirectory($archiveSource) | Out-Null
    [IO.File]::WriteAllText((Join-Path $archiveSource "credential.txt"), $canary)
    $archive = Join-Path $probeRoot "nested.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($archiveSource, $archive)
    if ((Invoke-Scanner @($archive)).ExitCode -eq 0) {
        throw "A synthetic credential inside an artifact archive passed the scan."
    }

    $override = Join-Path $probeRoot ".gitleaksignore"
    [IO.File]::WriteAllText($override, "synthetic override`n")
    if ((Invoke-Scanner @($probeRoot)).ExitCode -eq 0) {
        throw "A target-local Gitleaks ignore file bypassed the artifact scan."
    }
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}

Write-Host "Artifact secret-scan negative controls passed."
