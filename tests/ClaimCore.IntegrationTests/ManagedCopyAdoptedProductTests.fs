module ClaimCore.IntegrationTests.ManagedCopyAdoptedProductTests

open System
open System.Data
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAdoptionFixture

let private ct = CancellationToken.None

let internal origin owner (witness: WitnessProtocol) copyId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
    let tip = witness.Snapshot()

    let result =
        ManagedCopyAdoptionEvidence.verifyOrigin
            connection
            transaction
            witness
            tip.TipSequence
            copyId
            ct
        |> await

    transaction.Commit()

    result
    |> Option.defaultWith (fun () -> failtest "Verified adoption origin is missing"),
    tip

let internal draft (origin: VerifiedCopyAdoptionOrigin) (tip: Snapshot) kind state =
    {
        CopyId = origin.CopyId
        SourceCaseId = origin.CaseId
        AdoptionEventId = origin.AdoptionEventId
        ProducerKind = origin.ProducerKind
        OriginEventHash = origin.CopyEventHash
        EventId = Guid.NewGuid()
        Revision = origin.CopyRevision + 1L
        EventKind = kind
        State = state
        PreviousEventHash = origin.CopyEventHash
        ActionWitnessCutoffSequence = tip.TipSequence
        ActionWitnessCutoffHash = tip.TipHash
        VerificationProofSha256 = None
        LastVerifiedAt = None
        DeletionProofSha256 = None
        DeletionApprovalId = None
    }

let internal audit owner witness commitments =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    DataAudit.runWithSuppression connection witness (Some commitments) ct
    |> await
    |> ignore

let private purge owner (witness: WitnessProtocol) change caseId =
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    let draft = CaseLifecycleCandidate.draft caseId change
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (CaseErasurePurgeTests.syntheticInventory caseId)
            draft
            ct
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> commitments
    | _ -> failtest "Synthetic export purge failed."

let private adopted
    owner
    (witness: WitnessProtocol)
    commitments
    (runtime: Runtime)
    proposer
    caseId
    artifact
    =
    let fixture = fixtureDetails owner witness runtime proposer caseId artifact

    match (runtime.ForActor proposer).ApproveCopyAdoption(fixture.Request, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved _ -> ()
    | _ -> failtest "Synthetic adoption approval failed."

    match
        ManagedCopyAdoptionOwner.adopt
            owner
            witness
            commitments
            fixture.PrivateLocation
            fixture.Submission
            ct
        |> await
    with
    | CopyAdoptionOwnerOutcome.Adopted _ -> fixture
    | _ -> failtest "Synthetic adoption failed."

let private apply owner witness (fixture: AdoptionFixture) value =
    let canonical = ManagedCopyAdoptedTransitionAttestation.encode value

    let signature =
        fixture.SignatureAlgorithm.Sign(fixture.TransitionSigningKey, canonical)

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    ManagedCopyAdoptedTransitionAdministration.transition connection witness canonical signature
    |> await

let private corruptHistory owner witness commitments eventId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use changeEvent =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_events SET previous_hash=sha256(previous_hash) "
            + "WHERE event_id=@event",
            connection
        )

    Sql.uuid changeEvent "event" eventId
    Expect.equal (changeEvent.ExecuteNonQuery()) 1 "One synthetic history link changed"

    Expect.throws
        (fun () -> audit owner witness commitments)
        "Full audit rejects a changed post-adoption history link"

let private product
    owner
    _
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    _
    _
    _
    _
    change
    artifact
    caseId
    =
    let commitments = purge owner witness change caseId
    let fixture = adopted owner witness commitments runtime proposer caseId artifact
    use _key = fixture.TransitionSigningKey
    let verified, tip = origin owner witness fixture.Request.CopyId
    let unknown = draft verified tip "UNKNOWN" "UNKNOWN"
    let premature = draft verified tip "DELETE_REQUEST" "DELETE_PENDING"

    Expect.equal
        (apply owner witness fixture premature)
        AuthorityWriteOutcome.Refused
        "Retention still active keeps adopted product deletion pending"

    Expect.isNone
        (witness.EvidenceStore.TryReadEvidence(premature.EventId, ClaimCore.Witness.Intent))
        "Early deletion did not reserve witness authority"

    let forged = ManagedCopyAdoptedTransitionAttestation.encode unknown
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    Expect.equal
        (ManagedCopyAdoptedTransitionAdministration.transition
            connection
            witness
            forged
            (Array.zeroCreate<byte> 64)
         |> await)
        AuthorityWriteOutcome.Refused
        "A forged adopted-copy signature writes no authority"

    match apply owner witness fixture unknown with
    | AuthorityWriteOutcome.Applied(id, 3L) when id = unknown.EventId -> ()
    | _ -> failtest "Signed adopted-copy UNKNOWN failed."

    Expect.equal
        (apply owner witness fixture unknown)
        (AuthorityWriteOutcome.Applied(unknown.EventId, 3L))
        "Exact signed retry returns the witnessed transition"

    audit owner witness commitments

    corruptHistory owner witness commitments unknown.EventId

let tests =
    testList
        "adopted product copy transitions"
        [
            testCase
                "[CC-AUDIT-001] product export signed post-adoption transition replays through projection"
                (fun _ -> CaseErasureArtifactTests.withArtifact product)
        ]
