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

    Assert-PolicyResult "valid upload" $valid $true

    $globUpload = $valid.Replace(
        'path: artifacts/output/',
        "path: |`n            artifacts/output/a.json`n            artifacts/output/b.json"
    )
    Assert-PolicyResult "scanned parent covers multiple upload paths" $globUpload $true

    $missingScan = [regex]::Replace(
        $valid,
        '(?ms)^      - name: Scan before upload\r?\n.*?(?=^      - name: Upload output)',
        ''
    )
    Assert-PolicyResult "missing scanner" $missingScan $false

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
    Assert-PolicyResult "added unscanned job" $newJob $false

    $extraUpload = $valid + @'

      - name: Upload without a scan guard
        if: always()
        uses: actions/upload-artifact@0123456789abcdef0123456789abcdef01234567
        with:
          path: artifacts/output/
'@
    Assert-PolicyResult "added unguarded upload in existing job" $extraUpload $false

    $removedGuard = $valid.Replace(
        "steps.artifact_scan.outcome == 'success'",
        'steps.artifact_scan.conclusion == ''success'''
    )
    Assert-PolicyResult "guard refers to conclusion" $removedGuard $false

    $removedGuard = $valid.Replace(
        "if: `${{ always() && steps.artifact_scan.outcome == 'success' }}",
        'if: always()'
    )
    Assert-PolicyResult "removed upload guard" $removedGuard $false

    $conditionalScan = [regex]::Replace(
        $valid,
        '(?m)(        id: artifact_scan\r?\n        if: )always\(\)',
        '${1}success()'
    )
    Assert-PolicyResult "scanner skipped after failure" $conditionalScan $false

    $ignoredFailure = $valid.Replace(
        '        id: artifact_scan',
        "        id: artifact_scan`n        continue-on-error: true"
    )
    Assert-PolicyResult "scanner failure ignored" $ignoredFailure $false

    $duplicateScan = $valid.Replace(
        '      - name: Upload output',
        "      - name: Duplicate scan`n        id: artifact_scan`n      - name: Upload output"
    )
    Assert-PolicyResult "duplicate scanner id" $duplicateScan $false

    $wrongPath = $valid.Replace('path: artifacts/output/', 'path: artifacts/other/')
    Assert-PolicyResult "upload path outside scan scope" $wrongPath $false

    $writerStep = @'
      - name: Mutate output after scan
        run: touch artifacts/output/late.txt
      - name: Upload output
'@
    $interveningWriter = $valid.Replace('      - name: Upload output', $writerStep)
    Assert-PolicyResult "post-scan writer" $interveningWriter $false

    $commentOnly = $valid.Replace(
        'pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 `',
        '# pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1 `'
    )
    Assert-PolicyResult "commented-out scanner" $commentOnly $false

    $unknownExpression = $valid.Replace('path: artifacts/output/', 'path: artifacts/${{ matrix.unreviewed }}/')
    Assert-PolicyResult "unreviewed path expression" $unknownExpression $false

    $oddStructure = $valid.Replace('        uses: actions/upload-artifact@', '          uses: actions/upload-artifact@')
    Assert-PolicyResult "unparsed upload structure" $oddStructure $false
}
finally {
    if ([IO.Directory]::Exists($probeRoot)) { [IO.Directory]::Delete($probeRoot, $true) }
}

Write-Host "Artifact upload policy negative controls passed."
