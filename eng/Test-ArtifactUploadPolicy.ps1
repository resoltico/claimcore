[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$checker = Join-Path $PSScriptRoot "Check-ArtifactUploadPolicy.ps1"
$pwsh = (Get-Process -Id $PID).Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-artifact-policy-" + [Guid]::NewGuid().ToString("N"))
$workflowPath = Join-Path $probeRoot "fixture.yml"

$valid = @'
name: Isolated artifact policy probe
on: workflow_dispatch
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - name: Produce output
        run: mkdir -p artifacts/output
      - name: Scan before upload
        id: artifact_scan
        if: always()
        shell: pwsh
        run: |
          pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 `
            artifacts/output/
      - name: Upload output
        if: ${{ always() && steps.artifact_scan.outcome == 'success' }}
        uses: actions/upload-artifact@0123456789abcdef0123456789abcdef01234567
        with:
          path: artifacts/output/
          if-no-files-found: error
'@

function Assert-PolicyResult {
    param([string] $Case, [string] $Workflow, [bool] $Accept)

    [IO.File]::WriteAllText($workflowPath, $Workflow, [Text.UTF8Encoding]::new($false))
    $output = & $pwsh -NoProfile -File $checker -WorkflowRoot $probeRoot 2>&1
    $passed = $LASTEXITCODE -eq 0
    if ($passed -ne $Accept) {
        throw "Artifact upload policy probe '$Case' returned an unexpected result: $($output -join ' ')"
    }
}

try {
    [IO.Directory]::CreateDirectory($probeRoot) | Out-Null

    Assert-PolicyResult -Case "valid upload" -Workflow $valid -Accept $true

    $globUpload = $valid.Replace(
        'path: artifacts/output/',
        "path: |`n            artifacts/output/a.json`n            artifacts/output/b.json"
    )
    Assert-PolicyResult -Case "scanned parent covers multiple upload paths" -Workflow $globUpload -Accept $true

    $missingScan = [regex]::Replace(
        $valid,
        '(?ms)^      - name: Scan before upload\r?\n.*?(?=^      - name: Upload output)',
        ''
    )
    Assert-PolicyResult -Case "missing scanner" -Workflow $missingScan -Accept $false

    $newJob = $valid + @'

  added:
    runs-on: ubuntu-latest
    steps:
      - name: Upload unscanned output
        if: always()
        uses: actions/upload-artifact@0123456789abcdef0123456789abcdef01234567
        with:
          path: artifacts/other/
'@
    Assert-PolicyResult -Case "added unscanned job" -Workflow $newJob -Accept $false

    $extraUpload = $valid + @'

      - name: Upload without a scan guard
        if: always()
        uses: actions/upload-artifact@0123456789abcdef0123456789abcdef01234567
        with:
          path: artifacts/output/
'@
    Assert-PolicyResult -Case "added unguarded upload in existing job" -Workflow $extraUpload -Accept $false

    $removedGuard = $valid.Replace(
        "steps.artifact_scan.outcome == 'success'",
        'steps.artifact_scan.conclusion == ''success'''
    )
    Assert-PolicyResult -Case "guard refers to conclusion" -Workflow $removedGuard -Accept $false

    $removedGuard = $valid.Replace(
        "if: `${{ always() && steps.artifact_scan.outcome == 'success' }}",
        'if: always()'
    )
    Assert-PolicyResult -Case "removed upload guard" -Workflow $removedGuard -Accept $false

    $conditionalScan = [regex]::Replace(
        $valid,
        '(?m)(        id: artifact_scan\r?\n        if: )always\(\)',
        '${1}success()'
    )
    Assert-PolicyResult -Case "scanner skipped after failure" -Workflow $conditionalScan -Accept $false

    $ignoredFailure = $valid.Replace(
        '        id: artifact_scan',
        "        id: artifact_scan`n        continue-on-error: true"
    )
    Assert-PolicyResult -Case "scanner failure ignored" -Workflow $ignoredFailure -Accept $false

    $duplicateScan = $valid.Replace(
        '      - name: Upload output',
        "      - name: Duplicate scan`n        id: artifact_scan`n      - name: Upload output"
    )
    Assert-PolicyResult -Case "duplicate scanner id" -Workflow $duplicateScan -Accept $false

    $wrongPath = $valid.Replace('path: artifacts/output/', 'path: artifacts/other/')
    Assert-PolicyResult -Case "upload path outside scan scope" -Workflow $wrongPath -Accept $false

    $writerStep = @'
      - name: Mutate output after scan
        run: touch artifacts/output/late.txt
      - name: Upload output
'@
    $interveningWriter = $valid.Replace('      - name: Upload output', $writerStep)
    Assert-PolicyResult -Case "post-scan writer" -Workflow $interveningWriter -Accept $false

    $commentOnly = $valid.Replace(
        'pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 `',
        '# pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 `'
    )
    Assert-PolicyResult -Case "commented-out scanner" -Workflow $commentOnly -Accept $false

    $unknownExpression = $valid.Replace('path: artifacts/output/', 'path: artifacts/${{ matrix.unreviewed }}/')
    Assert-PolicyResult -Case "unreviewed path expression" -Workflow $unknownExpression -Accept $false

    $oddStructure = $valid.Replace('        uses: actions/upload-artifact@', '          uses: actions/upload-artifact@')
    Assert-PolicyResult -Case "unparsed upload structure" -Workflow $oddStructure -Accept $false
} finally {
    if ([IO.Directory]::Exists($probeRoot)) { [IO.Directory]::Delete($probeRoot, $true) }
}

Write-Host "Artifact upload policy negative controls passed."
