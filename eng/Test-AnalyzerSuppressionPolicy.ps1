[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$checker = Join-Path $PSScriptRoot "Check-AnalyzerSuppressions.ps1"
$nonce = [Guid]::NewGuid().ToString("N")
$sourceProbe = Join-Path $repoRoot "src/ClaimCore.Domain/SuppressionPolicyProbe-$nonce.fs"
$projectProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.props"
$typescriptProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.ts"
$javascriptProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.mjs"
$stylesheetProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.css"
$coverageProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.coverage.mjs"
$prettierProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.prettier.ts"
$configProbe = Join-Path $repoRoot "web/eslint.config.$nonce.mjs"
$focusedTestProbe = Join-Path $repoRoot "web/tests/FocusedPolicyProbe-$nonce.test.ts"
$conditionalTestProbe = Join-Path $repoRoot "web/tests/ConditionalPolicyProbe-$nonce.test.ts"
$annotationTestProbe = Join-Path $repoRoot "web/e2e/AnnotationPolicyProbe-$nonce.spec.ts"
$fsharpFocusedTestProbe = Join-Path $repoRoot "tests/ClaimCore.Tests/FocusedPolicyProbe-$nonce.fs"
$fsharpSkippedTestProbe = Join-Path $repoRoot "tests/ClaimCore.Tests/SkippedPolicyProbe-$nonce.fs"
$workflowFilterProbe = Join-Path $repoRoot ".github/workflows/TestFilterPolicyProbe-$nonce.yml"
$godFileProbe = Join-Path $repoRoot "eng/SuppressionPolicyProbe-$nonce.large.css"
$workflowGodFileProbe = Join-Path $repoRoot ".github/workflows/SuppressionPolicyProbe-$nonce.yml"
$namedEscapeDirectory = Join-Path $repoRoot "tests/coverage"
$namedEscapeProbe = Join-Path $namedEscapeDirectory "SuppressionPolicyProbe-$nonce.fs"
$knipEscapeDirectory = Join-Path $repoRoot "web/policy-probe-$nonce"
$knipEscapeProbe = Join-Path $knipEscapeDirectory "knip.json"
$retryTestProbe = Join-Path $repoRoot "web/e2e/RetryPolicyProbe-$nonce.spec.ts"
$pwsh = (Get-Process -Id $PID).Path

function Assert-Rejected {
    param([string] $Expected)

    $output = @(& $pwsh -NoProfile -File $checker 2>&1)
    $status = $LASTEXITCODE
    $text = $output -join [Environment]::NewLine
    if ($status -eq 0 -or -not $text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "Suppression policy negative control '$Expected' did not fail for the intended reason."
    }
}

try {
    $source = @'
namespace SuppressionPolicyProbe

open System.Diagnostics.CodeAnalysis

[<SuppressMessage(
    "Policy",
    "RULE-PROBE",
    Justification = "rationale: temporary multiline negative control")>]
let value = 1
'@
    [IO.File]::WriteAllText($sourceProbe, $source)
    Assert-Rejected "RULE-PROBE"
    [IO.File]::Delete($sourceProbe)

    $propertyName = "No" + "Warn"
    $project = "<Project><PropertyGroup><$propertyName>`nFS-PROBE`n</$propertyName></PropertyGroup></Project>"
    [IO.File]::WriteAllText($projectProbe, $project)
    Assert-Rejected "FS-PROBE"
    [IO.File]::Delete($projectProbe)

    [IO.File]::WriteAllText($typescriptProbe, "// @ts-expect-error`nconst value: string = 1;`n")
    Assert-Rejected "@ts-expect-error"
    [IO.File]::Delete($typescriptProbe)

    [IO.File]::WriteAllText($javascriptProbe, "// eslint-disable-next-line no-console`nconsole.log('probe');`n/* c8 ignore next */`nexport const value = 1;`n")
    Assert-Rejected "no-console"
    [IO.File]::Delete($javascriptProbe)

    [IO.File]::WriteAllText($stylesheetProbe, "/* stylelint-disable selector-max-id */`n#probe { color: red; }`n")
    Assert-Rejected "selector-max-id"
    [IO.File]::Delete($stylesheetProbe)

    [IO.File]::WriteAllText($coverageProbe, "/* c8 ignore next */`nexport const value = 1;`n")
    Assert-Rejected "c8-ignore-next"
    [IO.File]::Delete($coverageProbe)

    [IO.File]::WriteAllText($prettierProbe, "// prettier-ignore`nconst  value=1;`n")
    Assert-Rejected "prettier-ignore"
    [IO.File]::Delete($prettierProbe)

    [IO.File]::WriteAllText($configProbe, "export default [{ ignores: ['generated.ts'] }];`n")
    Assert-Rejected "eslint-config-ignore"
    [IO.File]::Delete($configProbe)

    [IO.File]::WriteAllText($focusedTestProbe, "test.only('probe', () => {});`n")
    Assert-Rejected "focused, skipped"
    [IO.File]::Delete($focusedTestProbe)

    [IO.File]::WriteAllText($conditionalTestProbe, "test.runIf(true)('probe', () => {});`n")
    Assert-Rejected "expected-failure, or conditional"
    [IO.File]::Delete($conditionalTestProbe)

    [IO.File]::WriteAllText($annotationTestProbe, "test('probe', { annotation: 'escape' }, async () => {});`n")
    Assert-Rejected "annotation or outcome escape"
    [IO.File]::Delete($annotationTestProbe)

    [IO.File]::WriteAllText($retryTestProbe, "testInfo.fail();`n")
    Assert-Rejected "outcome escape"
    [IO.File]::WriteAllText($retryTestProbe, "test.describe.configure({ retries: 1 });`n")
    Assert-Rejected "retry or expected-failure escape"
    [IO.File]::Delete($retryTestProbe)

    [IO.Directory]::CreateDirectory($knipEscapeDirectory) | Out-Null
    [IO.File]::WriteAllText($knipEscapeProbe, '{ "ignoreDependencies": ["probe"] }')
    Assert-Rejected "knip-ignore"
    [IO.File]::Delete($knipEscapeProbe)
    [IO.Directory]::Delete($knipEscapeDirectory)

    [IO.File]::WriteAllText($fsharpFocusedTestProbe, "module FocusedPolicyProbe`nopen Expecto`nlet tests = ftestCase ``probe`` (fun () -> ())`n")
    Assert-Rejected "focused or skipped F# test"
    [IO.File]::Delete($fsharpFocusedTestProbe)

    [IO.File]::WriteAllText($fsharpSkippedTestProbe, "module SkippedPolicyProbe`nopen Expecto`nlet tests = testCase ``probe`` (fun () -> skiptest ``probe``)`n")
    Assert-Rejected "focused or skipped F# test"
    [IO.File]::Delete($fsharpSkippedTestProbe)

    [IO.Directory]::CreateDirectory($namedEscapeDirectory) | Out-Null
    [IO.File]::WriteAllText($namedEscapeProbe, "module NamedEscapeProbe`n#nowarn `"9999`"`nlet value = 1`n")
    Assert-Rejected "FS9999"
    [IO.File]::WriteAllText($namedEscapeProbe, "module DiagnosticProbe`nlet format value = sprintf `"%A`" value`n")
    Assert-Rejected "structural formatting"
    [IO.File]::Delete($namedEscapeProbe)
    [IO.Directory]::Delete($namedEscapeDirectory)

    [IO.File]::WriteAllText($workflowFilterProbe, "jobs:`n  probe:`n    steps:`n      - run: dotnet test --filter Probe`n")
    Assert-Rejected "required-CI test filter"
    [IO.File]::Delete($workflowFilterProbe)

    $longStylesheet = [string]::Join([Environment]::NewLine, @(1..301 | ForEach-Object { "/* policy probe $_ */" }))
    [IO.File]::WriteAllText($godFileProbe, $longStylesheet)
    Assert-Rejected "physical lines"
    [IO.File]::Delete($godFileProbe)

    $longWorkflow = [string]::Join([Environment]::NewLine, @(1..301 | ForEach-Object { "# workflow policy probe $_" }))
    [IO.File]::WriteAllText($workflowGodFileProbe, $longWorkflow)
    Assert-Rejected "physical lines"
}
finally {
    @(
        $sourceProbe,
        $projectProbe,
        $typescriptProbe,
        $javascriptProbe,
        $stylesheetProbe,
        $coverageProbe,
        $prettierProbe,
        $configProbe,
        $focusedTestProbe,
        $conditionalTestProbe,
        $annotationTestProbe,
        $fsharpFocusedTestProbe,
        $fsharpSkippedTestProbe,
        $workflowFilterProbe,
        $godFileProbe,
        $workflowGodFileProbe,
        $namedEscapeProbe,
        $knipEscapeProbe,
        $retryTestProbe
    ) | Where-Object { Test-Path $_ -PathType Leaf } | ForEach-Object { [IO.File]::Delete($_) }
    if (Test-Path $namedEscapeDirectory -PathType Container) {
        [IO.Directory]::Delete($namedEscapeDirectory, $true)
    }
    if (Test-Path $knipEscapeDirectory -PathType Container) {
        [IO.Directory]::Delete($knipEscapeDirectory, $true)
    }
}

& $pwsh -NoProfile -File $checker
if ($LASTEXITCODE -ne 0) {
    throw "Suppression policy positive control failed after temporary fixtures were removed."
}

Write-Host "Analyzer suppression policy negative controls passed."
