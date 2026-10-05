module ClaimCore.IntegrationTests.CaseWitnessPayloadPruneRetryTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneRotation

let private ct = CancellationToken.None

let private ownerWitness (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private livePurgedReview
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    =
    let _, input, livePurge =
        CaseErasurePurgeTests.proposal runtime proposer first second

    let caseId = CaseErasurePurgeTests.caseId owner input.CaseReference
    let draft = CaseLifecycleCandidate.draft caseId livePurge
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
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
    | OwnerPurgeOutcome.Purged _ -> ()
    | _ -> failtest "Live-purge prerequisite failed."

    let review =
        match (runtime.ForActor first).Tombstones.Review(caseId, ct) |> await with
        | TombstoneReviewOutcome.Available value -> value
        | _ -> failtest "Tombstone review failed."

    caseId, commitments, review

let private prepared owner (witness: WitnessProtocol) (runtime: Runtime) proposer first second =
    let caseId, commitments, review =
        livePurgedReview owner witness runtime proposer first second

    let expires = DateTimeOffset.UtcNow.AddHours(1.0)

    let action =
        {
            EventId = Guid.NewGuid()
            CaseId = caseId
            PurgeEventId = review.PurgeEventId
            PurgeWitnessSequence = review.PurgeWitnessSequence
            PurgeWitnessEpoch = review.PurgeWitnessEpoch
            PurgeWitnessHash = review.PurgeWitnessHash
            CutoffSequence = review.CutoffSequence
            CutoffHash = review.CutoffHash
            TargetCount = review.TargetCount
            TargetDigest = review.TargetDigest
            ExpectedAuthorityRevision = review.AuthorityRevision
            ExpectedAuthorityHash = review.AuthorityHash
            ValidUntil = DateTimeOffset(expires.UtcTicks - expires.UtcTicks % 10L, TimeSpan.Zero)
        }

    for steward in [ first; second ] do
        let id = Guid.NewGuid()

        match
            (runtime.ForActor steward)
                .Tombstones.ApproveWitnessPrune(action, id, action.ValidUntil.AddMinutes(-1.0), ct)
            |> await
        with
        | TombstoneWriteOutcome.Applied(value, _) when value = id -> ()
        | _ -> failtest "Exact prune approval failed."

    caseId, action, commitments

let private partial owner writer (witness: WitnessProtocol) caseId action commitments =
    let inventory = CaseErasurePurgeTests.syntheticInventory caseId

    let execute connection proposal =
        CaseTombstonePruneOwner.execute owner connection witness commitments inventory proposal ct
        |> await

    match execute writer action with
    | OwnerWitnessPruneOutcome.Unconfirmed id when id = action.EventId -> ()
    | _ -> failtest "Denied owner settlement was not unconfirmed."

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use receipt =
        new NpgsqlCommand(
            "SELECT witness_prune_event_id IS NOT NULL FROM "
            + "claimcore.case_erasure_tombstones WHERE case_id=@case",
            connection
        )

    Sql.uuid receipt "case" caseId
    Expect.isTrue (receipt.ExecuteScalar() :?> bool) "Primary accepted exact prune receipt"

    Expect.isSome
        ((witness.EvidenceStore
            .TryReadEvidence(action.EventId, Intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "Prune intent remains durable"

    Expect.isNone
        ((witness.EvidenceStore
            .TryReadEvidence(action.EventId, SettledAuthority, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "No false settlement was inferred"

    execute

let private exactRetry
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
    let caseId, action, commitments =
        prepared owner witness runtime proposer first second

    let execute = partial owner writer witness caseId action commitments

    let changed =
        { action with
            TargetCount = action.TargetCount + 1L
        }

    match execute (ownerWitness writer) changed with
    | OwnerWitnessPruneOutcome.Refused ClaimCore.Domain.LifecycleRefusal.ApprovalMismatch -> ()
    | _ -> failtest "Changed proposal did not refuse exact retry."

    match execute (ownerWitness writer) action with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, count) when
        id = action.EventId && count = action.TargetCount
        ->
        ()
    | _ -> failtest "Exact partial-state retry failed."

    match execute (ownerWitness writer) action with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, 0L) when id = action.EventId -> ()
    | _ -> failtest "Second exact retry diverged."

    use audit = new NpgsqlConnection(owner)
    audit.Open()

    DataAudit.runWithSuppression audit witness (Some commitments) ct
    |> await
    |> ignore

let private unexpectedCaseIntent
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
    let caseId, action, commitments =
        prepared owner witness runtime proposer first second

    let execute = partial owner writer witness caseId action commitments
    let unexpectedId = Guid.NewGuid()

    let candidate =
        CaseTombstoneCandidate.proposal { action with EventId = unexpectedId }

    (witness
        .BeginAuthority(unexpectedId, candidate, Some caseId, CancellationToken.None)
        .GetAwaiter()
        .GetResult())
    |> ignore

    match execute (ownerWitness writer) action with
    | OwnerWitnessPruneOutcome.Unconfirmed id when id = action.EventId -> ()
    | _ -> failtest "Unexpected CASE intent did not halt prune."

    Expect.isNone
        ((witness.EvidenceStore
            .TryReadEvidence(action.EventId, SettledAuthority, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "Closed postcutoff authority set did not settle"

let private rotationRetry
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
    let caseId, action, commitments =
        prepared owner witness runtime proposer first second

    let execute = partial owner writer witness caseId action commitments

    match execute (ownerWitness writer) action with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, count) when
        id = action.EventId && count = action.TargetCount
        ->
        ()
    | _ -> failtest "Initial prune settlement failed before rotation."

    withRotatedWitness (ownerWitness writer) writer witness (fun rotated ->
        let retry proposal =
            CaseTombstonePruneOwner.execute
                owner
                (ownerWitness writer)
                rotated
                commitments
                (CaseErasurePurgeTests.syntheticInventory caseId)
                proposal
                ct
            |> await

        match
            retry
                { action with
                    TargetCount = action.TargetCount + 1L
                }
        with
        | OwnerWitnessPruneOutcome.Refused ClaimCore.Domain.LifecycleRefusal.ApprovalMismatch -> ()
        | _ -> failtest "Changed retired-key proposal was accepted."

        match retry action with
        | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, 0L) when id = action.EventId -> ()
        | _ -> failtest "Historical prune retry after key rotation failed.")

let tests =
    testList
        "witness prune uncertainty"
        [
            testCase
                "[CC-ERASE-001] partial prune retries exact intent and refuses changed proposal"
                (fun _ -> CaseLifecycleStoreFixture.setup exactRetry)
            testCase
                "[CC-ERASE-001] unexpected postcutoff CASE intent prevents witness ciphertext deletion"
                (fun _ -> CaseLifecycleStoreFixture.setup unexpectedCaseIntent)
            testCase
                "[CC-ERASE-001] exact settled prune replay survives witness key rotation"
                (fun _ -> CaseLifecycleStoreFixture.setup rotationRetry)
        ]
