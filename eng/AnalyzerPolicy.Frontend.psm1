Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot "AnalyzerPolicy.Common.psm1") -Force

function Find-BrowserSuppressions {
    param(
        [IO.FileInfo[]] $SourceFiles,
        [string] $RepoRoot,
        [Collections.Generic.List[object]] $Occurrences
    )

    $browserSourceFiles = @(
        $SourceFiles |
            Where-Object { $_.Extension -in ".ts", ".tsx", ".js", ".mjs", ".cjs", ".css" }
    )
    foreach ($sourceFile in $browserSourceFiles) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $sourceFile.FullName).Replace('\', '/')
        $isTestSource = $relative -match '^web/(tests|e2e)/'
        $lines = [IO.File]::ReadAllLines($sourceFile.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            $number = $index + 1
            $context = if ($index -gt 0) { $lines[$index - 1] + " " + $line } else { $line }

            if ($line -match '(?i)(?:\/\/|\/\*+)\s*eslint-disable(?:-(?:next-line|line))?\s*(?<rules>[^*\r\n]*?)(?:\s+--.*)?(?:\*\/)?\s*$') {
                foreach ($rule in Get-RuleCodes $Matches.rules) {
                    Add-Occurrence $Occurrences $relative $rule "line:$number" $number $true $context
                }
            }
            if ($line -match '(?i)(?:\/\/|\/\*+)\s*(?<rule>@ts-(?:ignore|nocheck|expect-error))\b') {
                Add-Occurrence $Occurrences $relative $Matches.rule "line:$number" $number $true $context
            }
            if ($line -match '(?i)(?:\/\/|\/\*+)\s*(?<rule>prettier-ignore(?:-start|-end)?)\b') {
                Add-Occurrence $Occurrences $relative $Matches.rule "line:$number" $number $true $context
            }
            if ($line -match '(?i)(?:\/\/|\/\*+)\s*stylelint-disable(?:-(?:next-line|line))?\s*(?<rules>[^*\r\n]*?)(?:\s+--.*)?(?:\*\/)?\s*$') {
                foreach ($rule in Get-RuleCodes $Matches.rules) {
                    Add-Occurrence $Occurrences $relative $rule "line:$number" $number $true $context
                }
            }
            if ($line -match '(?i)(?:\/\/|\/\*+)\s*(?<tool>istanbul|c8|vitest)\s+ignore(?:\s+(?<mode>next|if|else|file|start|stop))?\b') {
                $mode = if ([string]::IsNullOrWhiteSpace($Matches.mode)) { "unspecified" } else { $Matches.mode.ToLowerInvariant() }
                Add-Occurrence $Occurrences $relative ("{0}-ignore-{1}" -f $Matches.tool.ToLowerInvariant(), $mode) "line:$number" $number $true $context
            }
            if ($isTestSource -and
                $line -match '(?i)\b(?:describe|it|test)(?:\.describe)?\.(?:only|skip|todo|fails|runIf|skipIf|fixme|fail|slow)\b|\b(?:fdescribe|fit|xdescribe|xit|xtest)\b') {
                throw "$relative`:$number contains focused, skipped, expected-failure, or conditional JavaScript/TypeScript test code; this policy is non-suppressible."
            }
            if ($isTestSource -and
                $line -match '(?i)\b(?:testInfo|test\.info\(\))\.(?:annotations|fail|fixme|skip|slow)\b|\bannotations?\s*:') {
                throw "$relative`:$number contains a non-suppressible test annotation or outcome escape."
            }
            if ($isTestSource -and
                $line -match '(?i)\b(?:describe|test)(?:\.describe)?\.configure\s*\(|\b(?:retry|retries|fails)\s*:') {
                throw "$relative`:$number contains a non-suppressible retry or expected-failure escape."
            }
        }
    }
}

function Find-ConfigurationSuppressions {
    param(
        [string] $RepoRoot,
        [Collections.Generic.List[string]] $GeneratedExclusions,
        [Collections.Generic.List[object]] $Occurrences
    )

    $warningProperty = "No" + "Warn"
    $notErrorsName = "WarningsNot" + "AsErrors"
    $forbiddenOptions = @(
        "--" + "ignore-files", "--" + "ignore-rule", "--" + "exclude-analyzers",
        "--" + "nowarn", "--" + "warnaserror-", $warningProperty, $notErrorsName
    )
    $activeConfiguration = @(
        @(Get-ChildItem (Join-Path $RepoRoot ".github"), (Join-Path $RepoRoot "eng") -Recurse -File |
            Where-Object { $_.Extension -in ".yml", ".yaml", ".ps1", ".psm1" })
        ([IO.FileInfo]::new((Join-Path $RepoRoot ".editorconfig")))
    )
    foreach ($configurationFile in $activeConfiguration) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $configurationFile.FullName).Replace('\', '/')
        $lines = [IO.File]::ReadAllLines($configurationFile.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            foreach ($option in $forbiddenOptions) {
                if ($line.Contains($option, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "$relative`:$($index + 1) configures analyzer exclusions outside the registry: $option"
                }
            }
            if ($line -match '(?i)dotnet_diagnostic\.(?<rule>[^.]+)\.severity\s*=\s*(none|silent)') {
                Add-Occurrence $Occurrences $relative $Matches.rule "project" ($index + 1) $false $line
            }
        }
    }

    $frontendRoot = Join-Path $RepoRoot "web"
    $excluded = Get-GeneratedExclusionPaths $RepoRoot $GeneratedExclusions
    $frontendConfiguration = @(
        Get-RepositoryFiles @($frontendRoot) $excluded |
            Where-Object {
                $_.Name -in ".eslintignore", ".prettierignore", ".stylelintignore", "package.json", "knip.json" -or
                $_.Name -like ".eslintrc*" -or $_.Name -like "eslint.config.*" -or
                $_.Name -like "stylelint.config.*" -or $_.Name -like "prettier.config.*" -or
                $_.Name -like "vite.config.*" -or $_.Name -like "vitest.config.*" -or
                $_.Name -like "tsconfig*.json"
            }
    )
    foreach ($configurationFile in $frontendConfiguration) {
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $configurationFile.FullName).Replace('\', '/')
        $lines = [IO.File]::ReadAllLines($configurationFile.FullName)
        $name = $configurationFile.Name
        $isEslintConfig = $name -like ".eslintrc*" -or $name -like "eslint.config.*"
        $isStylelintConfig = $name -like "stylelint.config.*" -or $name -like ".stylelintrc*"
        $isCoverageConfig = $name -like "vite.config.*" -or $name -like "vitest.config.*" -or $name -eq "package.json"
        $isKnipConfig = $name -eq "knip.json"

        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            $number = $index + 1
            $context = if ($index -gt 0) { $lines[$index - 1] + " " + $line } else { $line }

            if (($name -eq ".eslintignore" -or $name -eq ".prettierignore" -or $name -eq ".stylelintignore") -and
                $line -notmatch '^\s*(#|$)') {
                $rule = switch ($name) {
                    ".eslintignore" { "eslint-ignore-file" }
                    ".prettierignore" { "prettier-ignore-file" }
                    default { "stylelint-ignore-file" }
                }
                Add-Occurrence $Occurrences $relative $rule "line:$number" $number $true $context
            }
            if (($isEslintConfig -or $name -eq "package.json") -and
                $line -match '(?i)(?:\beslintIgnore\b|\bignorePatterns\b|\bignores\s*:|\bglobalIgnores\s*\()') {
                Add-Occurrence $Occurrences $relative "eslint-config-ignore" "line:$number" $number $true $context
            }
            if ($isStylelintConfig -and $line -match '(?i)\b(?:ignoreFiles|ignoreDisables)\b') {
                Add-Occurrence $Occurrences $relative "stylelint-config-ignore" "line:$number" $number $true $context
            }
            if ($isKnipConfig -and $line -match '(?i)"ignore[^"]*"\s*:') {
                Add-Occurrence $Occurrences $relative "knip-ignore" "line:$number" $number $false $context
            }
            if ($name -like "tsconfig*.json" -and $line -match '(?i)["'']exclude["'']\s*:') {
                Add-Occurrence $Occurrences $relative "typescript-config-exclude" "line:$number" $number $true $context
            }
            if ($isCoverageConfig -and
                $line -match '(?i)(?:coveragePathIgnorePatterns|coverage.*--exclude|(?:istanbul|c8).*--exclude|^\s*["'']?exclude(?:AfterRemap)?["'']?\s*:)') {
                Add-Occurrence $Occurrences $relative "coverage-config-exclude" "line:$number" $number $true $context
            }
            if ($name -eq "package.json" -and
                $line -match '(?i)--(?:passWithNoTests|changed|related|shard|grep(?:-invert)?)\b') {
                throw "$relative`:$number contains a non-suppressible test-selection escape."
            }
            if ($name -like "playwright.config.*" -and
                $line -match '(?i)\b(?:grep|grepInvert|shard|annotations?)\s*:') {
                throw "$relative`:$number contains a non-suppressible Playwright selection or annotation escape."
            }
            if ($name -like "playwright.config.*" -and
                $line -match '(?i)\bretries\s*:\s*(?!0\b)[0-9]+') {
                throw "$relative`:$number enables Playwright retries; required evidence must be zero-retry."
            }
            if (($name -like "vite.config.*" -or $name -like "vitest.config.*") -and
                $line -match '(?i)\bretry\s*:\s*(?!0\b)[0-9]+') {
                throw "$relative`:$number enables Vitest retries; required evidence must be zero-retry."
            }
            if ($line -match '(?i)(?:max-lines|max-lines-per-function|complexity)\s*:\s*(?:0|["'']off["''])') {
                throw "$relative`:$number disables a non-suppressible frontend god-file rule."
            }
            if ($isEslintConfig -and $line -match '["''](?<rule>[^"'']+)["'']\s*:\s*(?:["'']off["'']|0)') {
                Add-Occurrence $Occurrences $relative $Matches.rule "line:$number" $number $true $context
            }
            if ($isStylelintConfig -and $line -match '["''](?<rule>[^"'']+)["'']\s*:\s*null\b') {
                Add-Occurrence $Occurrences $relative $Matches.rule "line:$number" $number $true $context
            }
        }
    }
}

Export-ModuleMember -Function @("Find-BrowserSuppressions", "Find-ConfigurationSuppressions")
