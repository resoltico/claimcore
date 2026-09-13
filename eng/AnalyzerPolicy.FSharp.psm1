Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.Common.psm1") -Force

function Find-FSharpSuppressions {
    param(
        [IO.FileInfo[]] $SourceFiles,
        [string] $RepoRoot,
        [Collections.Generic.List[object]] $Occurrences
    )

    $fsharpSourceFiles = @($SourceFiles | Where-Object { $_.Extension -in ".fs", ".fsi", ".fsx" })
    foreach ($sourceFile in $fsharpSourceFiles) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $sourceFile.FullName).Replace('\', '/')
        $text = [IO.File]::ReadAllText($sourceFile.FullName)
        $lines = [IO.File]::ReadAllLines($sourceFile.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            $number = $index + 1
            $context = if ($index -gt 0) { $lines[$index - 1] + " " + $line } else { $line }
            if ($line -match '(?i)fsharplint\s*:\s*disable(?:-next-line|-line)?(?:\s+(?<rules>[A-Za-z0-9_. -]+))?\s*$') {
                foreach ($rule in Get-RuleCodes $Matches.rules) {
                    Add-Occurrence $Occurrences $relative $rule "line:$number" $number $true $context
                }
            }
            if ($line -match '(?i)#pragma\s+warning\s+disable\s*(?<rules>.*)$') {
                foreach ($rule in Get-RuleCodes $Matches.rules) {
                    Add-Occurrence $Occurrences $relative $rule "line:$number" $number $true $context
                }
            }
        }

        foreach ($match in [regex]::Matches($text, '(?m)#nowarn\s+(?<rules>(?:"[0-9]+"\s*)+)')) {
            $line = Get-LineNumber $text $match.Index
            $previous = if ($line -gt 1) { $lines[$line - 2] } else { "" }
            foreach ($quoted in [regex]::Matches($match.Groups["rules"].Value, '"(?<rule>[0-9]+)"')) {
                $rule = "FS$($quoted.Groups["rule"].Value)"
                Add-Occurrence $Occurrences $relative $rule "line:$line" $line $true ($previous + " " + $match.Value)
            }
        }
        foreach ($match in [regex]::Matches($text, '(?s)SuppressMessage\s*\(\s*"[^"]*"\s*,\s*"(?<rule>[^"]+)"(?<tail>.*?)\)')) {
            $line = Get-LineNumber $text $match.Index
            $previous = if ($line -gt 1) { $lines[$line - 2] } else { "" }
            Add-Occurrence $Occurrences $relative $match.Groups["rule"].Value "line:$line" $line $true ($previous + " " + $match.Value)
        }
    }
}

function Assert-NoFocusedOrSkippedFSharpTests {
    param([IO.FileInfo[]] $SourceFiles, [string] $RepoRoot)

    $forbidden = '(?i)(?:\[<\s*[FP]Tests(?:Attribute)?\s*>\]|\b(?:f|p)test(?:Case(?:Async|Task|WithCancel)?|Async|Task|Theory(?:Async|Task)?|List)?\b|\bskiptestf?\b|\bFocusState\.(?:Focused|Pending)\b)'
    $testRoot = (Join-Path $RepoRoot "tests") + [IO.Path]::DirectorySeparatorChar
    foreach ($sourceFile in @($SourceFiles | Where-Object { $_.Extension -in ".fs", ".fsi", ".fsx" })) {
        if (-not $sourceFile.FullName.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $relative = [IO.Path]::GetRelativePath($RepoRoot, $sourceFile.FullName).Replace('\', '/')
        $lines = [IO.File]::ReadAllLines($sourceFile.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $match = [regex]::Match($lines[$index], $forbidden)
            if ($match.Success) {
                throw "$relative`:$($index + 1) contains non-suppressible focused or skipped F# test code: $($match.Value)"
            }
        }
    }
}

function Assert-NoSensitiveTestDiagnostics {
    param([IO.FileInfo[]] $SourceFiles, [string] $RepoRoot)

    $testRoot = (Join-Path $RepoRoot "tests") + [IO.Path]::DirectorySeparatorChar
    foreach ($sourceFile in @($SourceFiles | Where-Object { $_.Extension -in ".fs", ".fsx" })) {
        if (-not $sourceFile.FullName.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $lines = [IO.File]::ReadAllLines($sourceFile.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            if ($lines[$index] -match '%[AO]') {
                $relative = [IO.Path]::GetRelativePath($RepoRoot, $sourceFile.FullName).Replace('\', '/')
                throw "$relative`:$($index + 1) contains non-suppressible structural formatting that can serialize test payloads."
            }
        }
    }
}

function Assert-NoRequiredCiTestFilters {
    param([string] $RepoRoot)

    $workflowRoot = Join-Path $RepoRoot ".github/workflows"
    if (-not (Test-Path $workflowRoot -PathType Container)) {
        return
    }

    $forbidden = '(?i)(?:--filter(?:-uid)?\b|--treenode-filter\b|\bTestCaseFilter\b|--grep(?:-invert)?\b|--shard\b|--last-failed\b|--only-changed\b)'
    foreach ($workflow in Get-ChildItem -LiteralPath $workflowRoot -File -Recurse) {
        if ($workflow.Extension -notin ".yml", ".yaml") {
            continue
        }

        $relative = [IO.Path]::GetRelativePath($RepoRoot, $workflow.FullName).Replace('\', '/')
        $lines = [IO.File]::ReadAllLines($workflow.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $match = [regex]::Match($lines[$index], $forbidden)
            if ($match.Success) {
                throw "$relative`:$($index + 1) contains a non-suppressible required-CI test filter: $($match.Value)"
            }
        }
    }
}

function Find-ProjectWarningSuppressions {
    param(
        [string] $RepoRoot,
        [Collections.Generic.List[string]] $GeneratedExclusions,
        [Collections.Generic.List[object]] $Occurrences
    )

    $warningProperty = "No" + "Warn"
    $notErrorsName = "WarningsNot" + "AsErrors"
    $roots = @("src", "tests", "eng") | ForEach-Object { Join-Path $RepoRoot $_ }
    $excluded = Get-GeneratedExclusionPaths $RepoRoot $GeneratedExclusions
    $projectFiles = @(
        Get-RepositoryFiles $roots $excluded | Where-Object { $_.Extension -in ".fsproj", ".props", ".targets" }
        Get-ChildItem -LiteralPath $RepoRoot -File | Where-Object { $_.Extension -in ".props", ".targets" }
    )
    foreach ($projectFile in $projectFiles) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $projectFile.FullName).Replace('\', '/')
        [xml] $xml = [IO.File]::ReadAllText($projectFile.FullName)
        $xpath = "//*[local-name()='$warningProperty' or local-name()='$notErrorsName'] | //@$warningProperty | //@$notErrorsName"
        foreach ($node in $xml.SelectNodes($xpath)) {
            foreach ($rule in Get-RuleCodes $node.InnerText) {
                Add-Occurrence $Occurrences $relative $rule "project" 1 $false $node.InnerText
            }
        }
    }
}

function Assert-FSharpLintPolicy {
    param([string] $RepoRoot, [Collections.Generic.List[string]] $GeneratedExclusions)

    $lintConfig = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Join-Path $RepoRoot "fsharplint.json")))
    try {
        $ignoreFiles = @(
            $lintConfig.RootElement.GetProperty("ignoreFiles").EnumerateArray() |
                ForEach-Object { $_.GetString() }
        )
        if (Compare-Object @($GeneratedExclusions) $ignoreFiles -CaseSensitive) {
            throw "fsharplint.json ignoreFiles must exactly match generatedExclusions in analyzer-suppressions.json."
        }
        foreach ($property in $lintConfig.RootElement.EnumerateObject()) {
            if ($property.Value.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $enabled = [Text.Json.JsonElement]::new()
                if ($property.Value.TryGetProperty("enabled", [ref] $enabled) -and -not $enabled.GetBoolean()) {
                    throw "fsharplint.json disables '$($property.Name)' outside the registry."
                }
            }
        }
        Assert-NoNestedConfigExclusions $lintConfig.RootElement ""

        $fsharpGodFileLimits = @(
            [pscustomobject]@{ Rule = "cyclomaticComplexity"; Property = "maxComplexity"; Maximum = 12 },
            [pscustomobject]@{ Rule = "maxLinesInFile"; Property = "maxLinesInFile"; Maximum = 300 },
            [pscustomobject]@{ Rule = "maxLinesInModule"; Property = "maxLines"; Maximum = 300 },
            [pscustomobject]@{ Rule = "maxLinesInFunction"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInLambdaFunction"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInMatchLambdaFunction"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInValue"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInMember"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInConstructor"; Property = "maxLines"; Maximum = 50 },
            [pscustomobject]@{ Rule = "maxLinesInProperty"; Property = "maxLines"; Maximum = 50 }
        )
        foreach ($limit in $fsharpGodFileLimits) {
            $rule = $lintConfig.RootElement.GetProperty($limit.Rule)
            if (-not $rule.GetProperty("enabled").GetBoolean()) {
                throw "fsharplint.json must keep '$($limit.Rule)' enabled."
            }
            $value = $rule.GetProperty("config").GetProperty($limit.Property).GetInt32()
            if ($value -gt $limit.Maximum) {
                throw "fsharplint.json weakens '$($limit.Rule)' to $value; the maximum is $($limit.Maximum)."
            }
        }
    }
    finally {
        $lintConfig.Dispose()
    }
}

Export-ModuleMember -Function @(
    "Find-FSharpSuppressions", "Find-ProjectWarningSuppressions", "Assert-FSharpLintPolicy",
    "Assert-NoFocusedOrSkippedFSharpTests", "Assert-NoSensitiveTestDiagnostics", "Assert-NoRequiredCiTestFilters"
)
