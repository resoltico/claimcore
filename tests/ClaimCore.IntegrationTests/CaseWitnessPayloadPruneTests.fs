module ClaimCore.IntegrationTests.CaseWitnessPayloadPruneTests

open Expecto
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneControls

let internal runWithBeforeRequest
    beforeRequest
    owner
    source
    witness
    runtime
    proposer
    first
    second
    ungranted
    writer
    =
    let fixture =
        prepare beforeRequest owner source witness runtime proposer first second writer

    assertFirstPrune fixture
    assertPrunedMetadata fixture
    fullAudit fixture
    assertExactRetry fixture
    let status = review fixture
    holdReleaseAndRetry fixture status
    assertReadOnlyProof fixture
    assertRuntimeIsolation fixture ungranted

let private run owner source witness runtime proposer first second ungranted writer =
    runWithBeforeRequest
        (fun _ _ -> ())
        owner
        source
        witness
        runtime
        proposer
        first
        second
        ungranted
        writer

let tests =
    testList
        "witness payload prune"
        [
            testCase
                "[CC-ERASE-001] owner prune removes only sealed CASE ciphertext atomically"
                (fun _ -> CaseLifecycleStoreFixture.setup run)
        ]
