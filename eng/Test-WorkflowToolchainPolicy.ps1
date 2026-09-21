[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$checker = Join-Path $PSScriptRoot "Check-WorkflowToolchainPolicy.ps1"

function New-Probe {
    param([string] $Workflow, [string] $Action = "")

    $root = Join-Path ([IO.Path]::GetTempPath()) ("claimcore-workflow-policy-" + [Guid]::NewGuid().ToString("N"))
    $null = New-Item -ItemType Directory -Path (Join-Path $root ".github/workflows") -Force
    [IO.File]::WriteAllText((Join-Path $root ".github/workflows/probe.yml"), $Workflow)

    if (-not [string]::IsNullOrEmpty($Action)) {
        $null = New-Item -ItemType Directory -Path (Join-Path $root ".github/actions/toolchain") -Force
        [IO.File]::WriteAllText((Join-Path $root ".github/actions/toolchain/action.yml"), $Action)
    }

    return $root
}

function Assert-Refused {
    param([string] $Label, [string] $Workflow, [string] $Action = "")

    $root = New-Probe -Workflow $Workflow -Action $Action
    try {
        & $checker -Root $root *> $null
        throw "The workflow toolchain policy accepted $Label."
    }
    catch {
        if ($_.Exception.Message -like "The workflow toolchain policy accepted*") { throw }
        Write-Output "Refused: $Label."
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force }
}

$compliant = @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v7.0.1
        with:
          persist-credentials: false
      - uses: ./.github/actions/toolchain
'@

$root = New-Probe -Workflow $compliant
try { & $checker -Root $root *> $null; Write-Output "Accepted: a compliant workflow." }
finally { Remove-Item -LiteralPath $root -Recurse -Force }

Assert-Refused "a workflow that selects .NET directly" @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v7.0.1
        with:
          persist-credentials: false
      - uses: actions/setup-dotnet@2222222222222222222222222222222222222222 # v6.0.0
'@

Assert-Refused "a workflow that selects Node directly" @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v7.0.1
        with:
          persist-credentials: false
      - uses: actions/setup-node@3333333333333333333333333333333333333333 # v7.0.0
'@

Assert-Refused "a checkout that persists credentials" @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v7.0.1
'@

Assert-Refused "an action pinned to a tag" @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
'@

Assert-Refused "a pin without a reviewed version comment" @'
jobs:
  probe:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111
        with:
          persist-credentials: false
'@

Assert-Refused "an unpinned action inside a composite action" -Workflow $compliant -Action @'
runs:
  using: composite
  steps:
    - uses: actions/setup-node@main
'@

Write-Output "Workflow toolchain policy negative controls passed."
