Set-StrictMode -Version Latest

function Get-RequiredText {
    param([Text.Json.JsonElement] $Element, [string] $Name)

    $property = $Element.GetProperty($Name)
    if ($property.ValueKind -ne [Text.Json.JsonValueKind]::String -or
        [string]::IsNullOrWhiteSpace($property.GetString())) {
        throw "Registry '$Name' must be nonblank text."
    }
    return $property.GetString()
}

function Get-OptionalDate {
    param([Text.Json.JsonElement] $Element, [string] $Name)

    $property = [Text.Json.JsonElement]::new()
    if (-not $Element.TryGetProperty($Name, [ref] $property)) {
        return $null
    }
    if ($property.ValueKind -ne [Text.Json.JsonValueKind]::String) {
        throw "Registry '$Name' must be an ISO yyyy-MM-dd date."
    }
    return [DateOnly]::ParseExact($property.GetString(), "yyyy-MM-dd")
}

function Assert-Governance {
    param([Text.Json.JsonElement] $Entry, [string] $Label)

    $owner = Get-RequiredText $Entry "owner"
    $rationale = Get-RequiredText $Entry "rationale"
    if ($owner.Length -lt 3 -or $rationale.Length -lt 20) {
        throw "$Label needs a named owner and substantive rationale."
    }
    $reviewOn = Get-OptionalDate $Entry "reviewOn"
    $expiresOn = Get-OptionalDate $Entry "expiresOn"
    if ($null -eq $reviewOn -and $null -eq $expiresOn) {
        throw "$Label requires reviewOn or expiresOn."
    }
    $today = [DateOnly]::FromDateTime([DateTime]::UtcNow)
    if ($null -ne $reviewOn -and $reviewOn -lt $today) {
        throw "$Label passed its review date $reviewOn."
    }
    if ($null -ne $expiresOn -and $expiresOn -lt $today) {
        throw "$Label expired on $expiresOn."
    }
}

function Get-RuleCodes {
    param([AllowNull()][string] $Value)

    $codes = @($Value -split '[,;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($codes.Count -eq 0) {
        return @("*")
    }
    return @($codes | ForEach-Object { if ($_ -match '^\d+$') { "FS$_" } else { $_ } })
}

function Get-LineNumber {
    param([string] $Text, [int] $Index)
    return 1 + [regex]::Matches($Text.Substring(0, $Index), "`n").Count
}

function Get-RepositoryFiles {
    param(
        [string[]] $Roots,
        [string[]] $ExcludedDirectories = @()
    )

    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ExcludedDirectories) {
        [void]$excluded.Add([IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar))
    }
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($root in $Roots) {
        if (Test-Path $root -PathType Container) {
            $pending.Enqueue([IO.Path]::GetFullPath($root))
        }
    }

    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.PSIsContainer) {
                $candidate = $item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
                if (-not $excluded.Contains($candidate)) {
                    $pending.Enqueue($item.FullName)
                }
            }
            elseif ($item -is [IO.FileInfo]) {
                Write-Output $item
            }
        }
    }
}

function Get-GeneratedExclusionPaths {
    param([string] $RepoRoot, [Collections.Generic.List[string]] $GeneratedExclusions)

    return @(
        $GeneratedExclusions | ForEach-Object {
            $relative = $_.TrimEnd('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
            [IO.Path]::GetFullPath((Join-Path $RepoRoot $relative))
        }
    )
}

function Get-PolicySourceFiles {
    param([string] $RepoRoot, [Collections.Generic.List[string]] $GeneratedExclusions)

    $roots = @("src", "tests", "web", "eng", ".github") |
        ForEach-Object { Join-Path $RepoRoot $_ } |
        Where-Object { Test-Path $_ -PathType Container }

    $excluded = Get-GeneratedExclusionPaths $RepoRoot $GeneratedExclusions
    return @(
        Get-RepositoryFiles $roots $excluded |
            Where-Object {
                $_.Extension -in ".fs", ".fsi", ".fsx", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".css", ".ps1", ".psm1", ".sh", ".yml", ".yaml"
            }
    )
}

function Assert-PhysicalFileSizeLimits {
    param([IO.FileInfo[]] $SourceFiles, [string] $RepoRoot)

    foreach ($sourceFile in $SourceFiles) {
        $lineCount = [IO.File]::ReadAllLines($sourceFile.FullName).Length
        if ($lineCount -gt 300) {
            $relative = [IO.Path]::GetRelativePath($RepoRoot, $sourceFile.FullName).Replace('\', '/')
            throw "$relative has $lineCount physical lines; the repository maximum is 300. Split the responsibility instead of suppressing the limit."
        }
    }
}

function Add-Occurrence {
    param(
        [Collections.Generic.List[object]] $Occurrences,
        [string] $File,
        [string] $Rule,
        [string] $Scope,
        [int] $Line,
        [bool] $Inline,
        [string] $ExplanationContext
    )

    if ($Inline) {
        if ($ExplanationContext -notmatch '(?i)(rationale|reason|justification)\s*[:=]\s*\S') {
            throw "$File`:$Line inline suppression $Rule needs an adjacent rationale/reason/justification comment."
        }

        $entryReference = [regex]::Escape("$File|$Rule|$Scope")
        if ($ExplanationContext -notmatch "(?i)suppression-registry\s*:\s*$entryReference") {
            throw "$File`:$Line inline suppression $Rule needs a nearby 'suppression-registry: $File|$Rule|$Scope' reference."
        }
    }
    $Occurrences.Add([pscustomobject]@{ File = $File; Rule = $Rule; Scope = $Scope; Line = $Line })
}

function Test-ActiveJsonValue {
    param([Text.Json.JsonElement] $Value)

    switch ($Value.ValueKind) {
        ([Text.Json.JsonValueKind]::True) { return $true }
        ([Text.Json.JsonValueKind]::Array) { return $Value.GetArrayLength() -gt 0 }
        ([Text.Json.JsonValueKind]::String) { return -not [string]::IsNullOrWhiteSpace($Value.GetString()) }
        default { return $false }
    }
}

function Assert-NoNestedConfigExclusions {
    param([Text.Json.JsonElement] $Element, [string] $Path)

    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        return
    }
    foreach ($property in $Element.EnumerateObject()) {
        $propertyPath = if ($Path) { "$Path.$($property.Name)" } else { $property.Name }
        if ($propertyPath -ne "ignoreFiles" -and
            $property.Name -match '(?i)(ignore|exclude|suppress|allowed)' -and
            (Test-ActiveJsonValue $property.Value)) {
            throw "fsharplint.json configures '$propertyPath' outside the central registry."
        }
        Assert-NoNestedConfigExclusions $property.Value $propertyPath
    }
}

Export-ModuleMember -Function @(
    "Get-RequiredText", "Get-OptionalDate", "Assert-Governance", "Get-RuleCodes", "Get-LineNumber",
    "Get-RepositoryFiles", "Get-GeneratedExclusionPaths", "Get-PolicySourceFiles", "Assert-PhysicalFileSizeLimits", "Add-Occurrence",
    "Test-ActiveJsonValue", "Assert-NoNestedConfigExclusions"
)
