module ClaimCore.IntegrationTests.ManagedCopyAdoptedTransitionTests

open System.Threading
open System
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.ManagedCopyAdoptedProductTests
open ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalCase
open ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalAdoption

let private earlyRefusal
    owner
    (witness: WitnessProtocol)
    (origin: VerifiedCopyAdoptionOrigin)
    tip
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    let premature = draft origin tip "DELETE_REQUEST" "DELETE_PENDING"
    let canonical = ManagedCopyAdoptedTransitionAttestation.encode premature
    let signature = algorithm.Sign(copyKey, canonical)
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    Expect.equal
        (ManagedCopyAdoptedTransitionAdministration.transition
            connection
            witness
            canonical
            signature
         |> await)
        AuthorityWriteOutcome.Refused
        "External copy retention blocks early deletion"

    Expect.isNone
        ((witness.EvidenceStore
            .TryReadEvidence(premature.EventId, ClaimCore.Witness.Intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "Early external deletion has no witness intent"

let private recordUnknown
    owner
    (witness: WitnessProtocol)
    (origin: VerifiedCopyAdoptionOrigin)
    tip
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    let uncertain = draft origin tip "UNKNOWN" "UNKNOWN"
    let canonical = ManagedCopyAdoptedTransitionAttestation.encode uncertain
    let signature = algorithm.Sign(copyKey, canonical)
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        ManagedCopyAdoptedTransitionAdministration.transition connection witness canonical signature
        |> await
    with
    | AuthorityWriteOutcome.Applied(id, 2L) when id = uncertain.EventId ->
        uncertain, canonical, signature
    | _ -> failtest "External signed UNKNOWN failed."

let private externalScenario
    retention
    complete
    owner
    _
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    _
    writer
    =
    let actor, input, caseId, publication, commitments, source =
        openPublished owner witness runtime proposer retention

    fenceAndPurge owner witness runtime first second actor input caseId commitments

    let copyKey, algorithm, verified, tip =
        adopt owner witness runtime proposer actor caseId source publication commitments

    use _copyKey = copyKey

    if retention > TimeSpan.FromMinutes(1.0) then
        earlyRefusal owner witness verified tip copyKey algorithm

    let uncertain, canonical, signature =
        recordUnknown owner witness verified tip copyKey algorithm

    audit owner witness commitments

    complete
        owner
        writer
        witness
        runtime
        proposer
        publication
        verified
        uncertain
        canonical
        signature
        copyKey
        algorithm
        commitments

let private external =
    externalScenario (TimeSpan.FromDays(1.0)) (fun _ _ _ _ _ _ _ _ _ _ _ _ _ -> ())

let private externalDeletion =
    externalScenario (TimeSpan.FromSeconds(5.0)) ManagedCopyAdoptedDeletionFixture.finish

let tests =
    testList
        "adopted external copy transitions"
        [
            testCase "[CC-AUDIT-001] adopted external signed origin and transition replay" (fun _ ->
                setup external)
            testCase
                "[CC-ERASE-001] adopted external exact signed absence completes deletion"
                (fun _ -> setup externalDeletion)
        ]
