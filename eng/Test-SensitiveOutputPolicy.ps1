[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$nonce = [Guid]::NewGuid().ToString("N")
$probeRoot = Join-Path $repoRoot "artifacts/sensitive-output-probe-$nonce"
$secretPath = Join-Path $probeRoot "private.secret"
$diagnosticPath = Join-Path $probeRoot "diagnostics/output.txt"
$checker = Join-Path $PSScriptRoot "Assert-NoSensitiveOutput.ps1"
$pwsh = (Get-Process -Id $PID).Path
$canary = "claimcore-sensitive-canary-$nonce"

try {
    [IO.Directory]::CreateDirectory((Split-Path $diagnosticPath)) | Out-Null
    [IO.File]::WriteAllText($secretPath, $canary, [Text.UTF8Encoding]::new($false))

    $variants = @(
        $canary,
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($canary)),
        [Uri]::EscapeDataString($canary)
    )

    foreach ($variant in $variants) {
        [IO.File]::WriteAllText($diagnosticPath, $variant, [Text.UTF8Encoding]::new($false))
        & $pwsh -NoProfile -File $checker -ScanRoot (Split-Path $diagnosticPath) -SecretFile $secretPath *> $null
        if ($LASTEXITCODE -eq 0) {
            throw "Sensitive-output negative control did not reject an encoded canary."
        }
    }

    [IO.File]::WriteAllText($diagnosticPath, "bounded sanitized diagnostic", [Text.UTF8Encoding]::new($false))
    & $pwsh -NoProfile -File $checker -ScanRoot (Split-Path $diagnosticPath) -SecretFile $secretPath
    if ($LASTEXITCODE -ne 0) {
        throw "Sensitive-output positive control rejected sanitized output."
    }
}
finally {
    if ([IO.Directory]::Exists($probeRoot)) {
        [IO.Directory]::Delete($probeRoot, $true)
    }
}

Write-Host "Sensitive-output policy negative controls passed."
