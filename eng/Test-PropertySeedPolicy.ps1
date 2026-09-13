[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$selector = Join-Path $PSScriptRoot "Select-PropertySeed.ps1"

function Scheduled {
    param([string] $Attempt)

    & $selector -EventName schedule -RequestedSeed "" -Repository owner/project `
        -Workflow verification -RunId 1234 -RunAttempt $Attempt
}

$first = Scheduled "1"
$repeated = Scheduled "1"
$secondAttempt = Scheduled "2"
if ($first -cne $repeated -or $first -ceq $secondAttempt) {
    throw "Scheduled property seeds are not deterministic and attempt-scoped."
}

$manual = & $selector -EventName workflow_dispatch -RequestedSeed "18446744073709551615" `
    -Repository owner/project -Workflow verification -RunId 1234 -RunAttempt 1
if ($manual -cne "18446744073709551615") {
    throw "A canonical manual property seed was not preserved."
}

try {
    & $selector -EventName workflow_dispatch -RequestedSeed "01" -Repository owner/project `
        -Workflow verification -RunId 1234 -RunAttempt 1 | Out-Null
    throw "A noncanonical manual seed was accepted."
}
catch {
    if ($_.Exception.Message -eq "A noncanonical manual seed was accepted.") { throw }
}

Write-Host "Property seed policy negative controls passed."
