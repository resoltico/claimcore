[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $ScanRoot,
    [Parameter(Mandatory = $true)]
    [string[]] $SecretFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-SecretVariant {
    param([string] $Path)

    $info = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    if (-not $info.Exists -or $info.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Sensitive-output scan input is not a regular secret file."
    }
    if ($info.Length -gt 65536) {
        throw "Sensitive-output scan input exceeds its bounded size."
    }

    $bytes = [IO.File]::ReadAllBytes($info.FullName)
    if ($bytes.Length -lt 8) {
        throw "Sensitive-output scan inputs must be at least eight characters."
    }

    $variants = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void] $variants.Add([Convert]::ToBase64String($bytes))
    [void] $variants.Add([Convert]::ToHexString($bytes).ToLowerInvariant())
    [void] $variants.Add([Convert]::ToHexString($bytes))

    try {
        $value = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).Trim()
        if ($value.Length -lt 8) {
            throw "Sensitive-output scan text inputs must be at least eight characters."
        }
        $json = ConvertTo-Json -InputObject $value -Compress
        [void] $variants.Add($value)
        [void] $variants.Add([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value)))
        [void] $variants.Add([Uri]::EscapeDataString($value))
        [void] $variants.Add($json.Substring(1, $json.Length - 2))
    } catch [Text.DecoderFallbackException] {
        # Raw capability files are binary; only their encoded forms can appear in text reports.
        Write-Verbose "A binary capability file contributes only its encoded forms."
    }

    return $variants
}

$variants = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $SecretFile) {
    foreach ($variant in Get-SecretVariant $path) {
        [void] $variants.Add($variant)
    }
}

$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($rootValue in $ScanRoot) {
    $root = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($rootValue))
    if (-not $root.Exists -or $root.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Sensitive-output scan root is not a regular directory."
    }

    foreach ($file in $root.EnumerateFiles("*", [IO.SearchOption]::AllDirectories)) {
        if ($file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Sensitive-output scan encountered a reparse point."
        }
        if ($file.Length -gt 16777216) {
            throw "Sensitive-output scan encountered an oversized diagnostic file."
        }
        $files.Add($file)
    }
}

foreach ($file in $files) {
    $content = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    foreach ($variant in $variants) {
        if ($content.Contains($variant, [StringComparison]::Ordinal)) {
            throw "Sensitive-output scan rejected a diagnostic artifact."
        }
    }
}

Write-Host "Sensitive-output scan passed for $($files.Count) diagnostic files."
