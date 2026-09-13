[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EventName,
    [AllowEmptyString()][string] $RequestedSeed,
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][string] $Workflow,
    [Parameter(Mandatory)][string] $RunId,
    [Parameter(Mandatory)][string] $RunAttempt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($EventName -eq "schedule") {
    $parts = @($Repository, $Workflow, $RunId, $RunAttempt)
    if ($parts | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 256 }) {
        throw "Scheduled property seed identity is incomplete or unbounded."
    }

    $material = [string]::Join([char]0, $parts)
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))
    [byte[]] $prefix = $hash[0..7]
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }
    $seed = [BitConverter]::ToUInt64($prefix, 0)
    Write-Output $seed.ToString([Globalization.CultureInfo]::InvariantCulture)
    exit 0
}

$parsed = [uint64]0
$canonical = [uint64]::TryParse(
    $RequestedSeed,
    [Globalization.NumberStyles]::None,
    [Globalization.CultureInfo]::InvariantCulture,
    [ref] $parsed
)
if (-not $canonical -or $parsed.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $RequestedSeed) {
    throw "The requested property seed must be one canonical unsigned 64-bit integer."
}

Write-Output $RequestedSeed
