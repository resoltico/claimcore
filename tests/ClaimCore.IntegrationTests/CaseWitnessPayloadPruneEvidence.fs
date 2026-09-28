module internal ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private ct = CancellationToken.None

[<NoEquality; NoComparison>]
type PruneFixture =
    {
        Owner: string
        App: string
        Writer: string
        Witness: WitnessProtocol
        Runtime: Runtime
        Actor: IActorClaimsCore
        Input: CommandRequest
        CaseId: Guid
        Action: TombstonePruneProposal
        Commitments: ISuppressionCommitments
        Steward: IActorClaimsCore
        StewardPrincipal: PrincipalKey
    }

let private proposal (value: TombstoneReview) =
    let expiry = DateTimeOffset.UtcNow.AddHours(1.0)

    let validUntil =
        DateTimeOffset(expiry.UtcTicks - expiry.UtcTicks % 10L, TimeSpan.Zero)

    {
        EventId = Guid.NewGuid()
        CaseId = value.CaseId
        PurgeEventId = value.PurgeEventId
        PurgeWitnessSequence = value.PurgeWitnessSequence
        PurgeWitnessEpoch = value.PurgeWitnessEpoch
        PurgeWitnessHash = value.PurgeWitnessHash
        CutoffSequence = value.CutoffSequence
        CutoffHash = value.CutoffHash
        TargetCount = value.TargetCount
        TargetDigest = value.TargetDigest
        ExpectedAuthorityRevision = value.AuthorityRevision
        ExpectedAuthorityHash = value.AuthorityHash
        ValidUntil = validUntil
    }

let private witnessOwner (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private approve (runtime: Runtime) steward request =
    let id = Guid.NewGuid()

    match
        (runtime.ForActor steward)
            .Tombstones.ApproveWitnessPrune(request, id, request.ValidUntil.AddMinutes(-1.0), ct)
        |> await
    with
    | TombstoneWriteOutcome.Applied(eventId, _) when eventId = id -> ()
    | _ -> failtest "Steward witness-prune approval failed."

let private purgedCase
    beforeRequest
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    =
    let actor, input, purge =
        CaseErasurePurgeTests.proposalWith beforeRequest runtime proposer first second

    let caseId = CaseErasurePurgeTests.caseId owner input.CaseReference
    let draft = CaseLifecycleCandidate.draft caseId purge
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
    | OwnerPurgeOutcome.Purged _ -> actor, input, caseId, commitments
    | _ -> failtest "Live purge prerequisite failed."

let prepare
    beforeRequest
    owner
    (_, app)
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    writer
    =
    let actor, input, caseId, commitments =
        purgedCase beforeRequest owner witness runtime proposer first second

    let reviewed =
        match (runtime.ForActor first).Tombstones.Review(caseId, ct) |> await with
        | TombstoneReviewOutcome.Available value -> value
        | _ -> failtest "Opaque tombstone review failed."

    let action = proposal reviewed
    approve runtime first action
    approve runtime second action

    {
        Owner = owner
        App = app
        Writer = writer
        Witness = witness
        Runtime = runtime
        Actor = actor
        Input = input
        CaseId = caseId
        Action = action
        Commitments = commitments
        Steward = runtime.ForActor first
        StewardPrincipal = first
    }

let execute (fixture: PruneFixture) =
    CaseTombstonePruneOwner.execute
        fixture.Owner
        (witnessOwner fixture.Writer)
        fixture.Witness
        fixture.Commitments
        (CaseErasurePurgeTests.syntheticInventory fixture.CaseId)
        fixture.Action
        ct
    |> await

let fullAudit (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    let summary =
        DataAudit.runWithSuppression connection fixture.Witness (Some fixture.Commitments) ct
        |> await

    Expect.equal summary.ErasureFences 1L "Postprune full audit retains pending tombstone"

let assertFirstPrune (fixture: PruneFixture) =
    let outcome = execute fixture

    match outcome with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, count) when
        id = fixture.Action.EventId && count = fixture.Action.TargetCount
        ->
        ()
    | _ ->
        let category =
            match outcome with
            | OwnerWitnessPruneOutcome.Refused _ -> "REFUSED"
            | OwnerWitnessPruneOutcome.ResourceUnavailable -> "RESOURCE_UNAVAILABLE"
            | OwnerWitnessPruneOutcome.InventoryUnknown -> "INVENTORY_UNKNOWN"
            | OwnerWitnessPruneOutcome.AuditUnavailable stage -> stage
            | OwnerWitnessPruneOutcome.Unconfirmed _ -> "UNCONFIRMED"
            | OwnerWitnessPruneOutcome.WitnessPayloadPruned _ -> "WRONG_RECEIPT"

        use connection = new NpgsqlConnection(fixture.Owner)
        connection.Open()

        use probe =
            new NpgsqlCommand(
                "SELECT witness_prune_event_id IS NOT NULL FROM "
                + "claimcore.case_erasure_tombstones WHERE case_id=@case",
                connection
            )

        Sql.uuid probe "case" fixture.CaseId
        let primary = probe.ExecuteScalar() :?> bool

        let intent =
            fixture.Witness.EvidenceStore.TryReadEvidence(fixture.Action.EventId, Intent).IsSome

        let settled =
            fixture.Witness.EvidenceStore
                .TryReadEvidence(fixture.Action.EventId, SettledAuthority)
                .IsSome

        failtestf
            "Owner CASE prune failed at %s (primary=%b,intent=%b,settled=%b)"
            category
            primary
            intent
            settled

let assertExactRetry (fixture: PruneFixture) =
    match execute fixture with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, 0L) when id = fixture.Action.EventId -> ()
    | _ -> failtest "Historical prune retry diverged."

let assertPrunedMetadata (fixture: PruneFixture) =
    let mutable after = 0L
    let mutable previous = Array.zeroCreate<byte> 32
    let mutable removed = 0L

    while after < fixture.Action.CutoffSequence do
        let page =
            fixture.Witness.EvidenceStore.ReadMetadataPage(
                after,
                previous,
                fixture.Action.CutoffSequence,
                32
            )

        for row in page.Items do
            if row.Ticket.SubjectCaseId = Some fixture.CaseId then
                Expect.isFalse row.PayloadPresent "Sealed CASE ciphertext is absent"
                removed <- removed + 1L

            after <- row.Ticket.Sequence
            previous <- row.Ticket.EntryHash

    Expect.equal
        removed
        fixture.Action.TargetCount
        "All selected CASE metadata remains without ciphertext"
