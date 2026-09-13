[CmdletBinding()]
param([string] $GitleaksPath = "gitleaks")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scanner = Join-Path $PSScriptRoot "Scan-SourceSecrets.ps1"
$pwsh = (Get-Process -Id $PID).Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-source-scan-policy-" + [Guid]::NewGuid().ToString("N"))
$canary =
    "aws_access_key_id = " + "AK" + "IA7JQ4N2P6R8T0V3X5" + [Environment]::NewLine +
    "aws_secret_access_key = " + "7Yk3pQ9v" + "L2mN8cR4" + "tW6xZ1aB" + "5dF0hJ7s" + "K9uE3iO6"
$gitRedirectVariables = @(
    "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CEILING_DIRECTORIES", "GIT_COMMON_DIR",
    "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_DIR", "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    "GIT_INDEX_FILE", "GIT_NAMESPACE", "GIT_OBJECT_DIRECTORY", "GIT_WORK_TREE"
)

function Invoke-Scanner {
    $output = @(
        & $pwsh -NoProfile -File $scanner -RepositoryRoot $probeRoot -GitleaksPath $GitleaksPath 2>&1
    )
    return $LASTEXITCODE
}

function Invoke-ProbeGit {
    param([string[]] $Arguments)

    $saved = @{}
    foreach ($name in $gitRedirectVariables) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
    try {
        $gitOutput = @(& git -C $probeRoot @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ($gitOutput -join " ")
            throw "Source-scan Git fixture command '$($Arguments[0])' failed with exit $LASTEXITCODE`: $detail"
        }
    }
    finally {
        foreach ($name in $gitRedirectVariables) {
            if ($null -eq $saved[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [Environment]::SetEnvironmentVariable($name, $saved[$name])
            }
        }
    }
}

try {
    [IO.Directory]::CreateDirectory((Join-Path $probeRoot "src")) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $probeRoot ".local")) | Out-Null
    [IO.File]::WriteAllText((Join-Path $probeRoot ".gitignore"), ".local/`nignored-secret.txt`n")
    [IO.File]::WriteAllText((Join-Path $probeRoot "src/safe.txt"), "synthetic safe source`n")
    [IO.File]::WriteAllText((Join-Path $probeRoot ".local/ignored.txt"), $canary)

    if ((Invoke-Scanner) -ne 0) {
        throw "The unversioned scan did not exclude ignored local state."
    }

    $ordinary = Join-Path $probeRoot "src/ordinary-secret.txt"
    [IO.File]::WriteAllText($ordinary, $canary)
    if ((Invoke-Scanner) -eq 0) {
        throw "The source scan did not detect an ordinary source canary."
    }
    [IO.File]::Delete($ordinary)

    $inlineAllow = Join-Path $probeRoot "src/inline-allow-secret.txt"
    $annotatedCanary = ($canary -replace [Environment]::NewLine, " # gitleaks:allow`n") + " # gitleaks:allow"
    [IO.File]::WriteAllText($inlineAllow, $annotatedCanary)
    if ((Invoke-Scanner) -eq 0) {
        throw "An inline Gitleaks allow comment bypassed the source scan."
    }
    [IO.File]::Delete($inlineAllow)

    Invoke-ProbeGit @("init", "--quiet")
    Invoke-ProbeGit @("add", ".gitignore", "src/safe.txt")
    if ((Invoke-Scanner) -ne 0) {
        throw "The Git scan did not exclude ignored untracked local state."
    }

    $forced = Join-Path $probeRoot "ignored-secret.txt"
    [IO.File]::WriteAllText($forced, $canary)
    Invoke-ProbeGit @("add", "--force", "ignored-secret.txt")
    if ((Invoke-Scanner) -eq 0) {
        throw "The source scan omitted a force-tracked ignored canary."
    }
}
finally {
    if ([IO.Directory]::Exists($probeRoot)) {
        [IO.Directory]::Delete($probeRoot, $true)
    }
}

Write-Host "Source secret-scan inventory negative controls passed."
