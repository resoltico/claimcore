module ClaimCore.IntegrationTests.CaseErasurePurgeTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private cancellation = CancellationToken.None

let internal caseId owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    Sql.text command "reference" reference
    command.ExecuteScalar() :?> Guid

let internal proposalWith
    (beforeRequest: IActorClaimsCore -> CommandRequest -> unit)
    (runtime: Runtime)
    proposer
    first
    second
    =
    let actor = runtime.ForActor proposer
    let input = openRequest (Guid.NewGuid()) ("PURGE-" + Guid.NewGuid().ToString("N"))
    executeAccepted actor input
    beforeRequest actor input
    let current = review actor input.CaseReference

    let request =
        change
            (Guid.NewGuid())
            input.CaseReference
            current
            (LifecycleMutation.RequestErasure "Synthetic live deletion request")

    actor.Lifecycle.Apply(request, cancellation) |> await |> ignore
    let requested = review actor input.CaseReference

    let pending =
        change
            (Guid.NewGuid())
            input.CaseReference
            requested
            (LifecycleMutation.MarkErasurePending "Synthetic fence checked")

    actor.Lifecycle.Apply(pending, cancellation) |> await |> ignore
    let current = review actor input.CaseReference
    let expiry = DateTimeOffset.UtcNow.AddHours 1.0

    let purge =
        change
            (Guid.NewGuid())
            input.CaseReference
            current
            (LifecycleMutation.PurgeLivePayload("Synthetic live purge reason", expiry))

    for steward in [ first; second ] do
        let approver = runtime.ForActor steward

        match
            approver.Lifecycle.Approve(purge, Guid.NewGuid(), expiry.AddMinutes(-1.0), cancellation)
            |> await
        with
        | LifecycleWriteOutcome.Applied _ -> ()
        | _ -> failtest "Exact steward approval failed."

    actor, input, purge

let internal proposal runtime proposer first second =
    proposalWith (fun _ _ -> ()) runtime proposer first second

let internal syntheticInventory (caseId: Guid) : IManagedCopyErasureClearance =
    { new IManagedCopyErasureClearance with
        member _.RequireCompleteInventory(primary, transaction, _, subject, cutoff, hash, _) =
            use clock = new NpgsqlCommand("SELECT clock_timestamp()", primary, transaction)

            let observed =
                match clock.ExecuteScalar() with
                | :? DateTimeOffset as value -> value
                | :? DateTime as value when value.Kind = DateTimeKind.Utc -> DateTimeOffset value
                | _ -> failtest "Synthetic inventory clock is unavailable"

            Task.FromResult(
                if subject <> caseId then
                    None
                else
                    Some
                        {
                            CaseId = subject
                            WitnessCutoffSequence = cutoff
                            WitnessCutoffHash = Array.copy hash
                            InventorySha256 =
                                SHA256.HashData(Encoding.ASCII.GetBytes("SYNTHETIC_ZERO_COPY"))
                            CopyCount = 0L
                            ObservedAt = observed
                        }
            )
    }

let private assertPurged
    owner
    (caseId: Guid)
    (reference: string)
    (reason: string)
    (commitments: ISuppressionCommitments)
    (draft: byte array)
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use tombstone =
        new NpgsqlCommand(
            "SELECT phase,identity_coverage,request_candidate_sha256,"
            + "request_candidate_commitment,purge_proposal_commitment,purge_canonical_action "
            + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
            connection
        )

    Sql.uuid tombstone "case" caseId
    use reader = tombstone.ExecuteReader()
    Expect.isTrue (reader.Read()) "The original request tombstone survives"
    Expect.equal (reader.GetString(0)) "ERASURE_PENDING" "No copy erasure is certified"
    Expect.equal (reader.GetString(1)) "WITNESS_COMPLETE" "Known intent IDs are sealed"
    Expect.isTrue (reader.IsDBNull(2)) "Bare request candidate SHA is cleared"
    Expect.equal (reader.GetFieldValue<byte array>(3).Length) 32 "Request is keyed"

    Expect.equal
        (reader.GetFieldValue<byte array>(4))
        (commitments.PurgeProposal draft)
        "Exact proposal is installation-keyed"

    let retained = Encoding.UTF8.GetString(reader.GetFieldValue<byte array>(5))

    Expect.isFalse
        (retained.Contains(reference, StringComparison.Ordinal))
        "Raw reference is absent"

    Expect.isFalse (retained.Contains(reason, StringComparison.Ordinal)) "Raw reason is absent"
    reader.Close()

    use live =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.cases WHERE case_id=@case", connection)

    Sql.uuid live "case" caseId
    Expect.equal (live.ExecuteScalar() :?> int64) 0L "Current claimant row is gone"

let private ordinaryDenied (actor: IActorClaimsCore) (input: CommandRequest) =
    match actor.Get(input.CaseReference, cancellation) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "Purged case became ordinarily readable"

    for candidate in
        [
            openRequest (Guid.NewGuid()) input.CaseReference
            openRequest input.OperationId ("OTHER-" + Guid.NewGuid().ToString("N"))
        ] do
        match actor.Prepare(candidate, cancellation) |> await with
        | PrepareOutcome.PrepareRejected(_, Rejection.ResourceUnavailable) -> ()
        | _ -> failtest "Old reference or operation identity was reused"

    match actor.Execute(input, cancellation) |> await with
    | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) -> ()
    | _ -> failtest "Exact accepted retry resurrected a purged receipt"

let private exactRetry
    (owner: string)
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (commitments: ISuppressionCommitments)
    (id: Guid)
    (change: LifecycleChange)
    (draft: byte array)
    =
    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (syntheticInventory id)
            draft
            cancellation
        |> await
    with
    | OwnerPurgeOutcome.Purged(eventId, 0L) when eventId = change.EventId -> ()
    | _ -> failtest "Exact purge retry diverged."

let private assertPostPurgeAudit
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (commitments: ISuppressionCommitments)
    =
    let report =
        DataAudit.runWithSuppression connection witness (Some commitments) cancellation
        |> await

    Expect.equal report.ErasureFences 1L "Purged tombstone is read-only audited"

let private assertOpaqueTombstone
    (source: NpgsqlDataSource)
    (runtime: Runtime)
    first
    id
    (change: LifecycleChange)
    (commitments: ISuppressionCommitments)
    =
    let gate = new PostgresActorGate(source, commitments) :> IActorGate

    let context =
        gate.Tombstone(first, EndpointAction.ReviewTombstone, id, cancellation) |> await

    Expect.isSome context "Current steward must be admitted to the opaque tombstone"

    match (runtime.ForActor first).Tombstones.Review(id, cancellation) |> await with
    | TombstoneReviewOutcome.Available review when
        review.CaseId = id
        && review.PurgeEventId = change.EventId
        && review.TargetCount > 0L
        && review.RequiredDistinctStewardApprovals = 2
        ->
        ()
    | _ -> failtest "Steward tombstone seal review was unavailable."

let private runSynthetic
    (owner: string)
    (source: NpgsqlDataSource, _)
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    _
    _
    =
    let actor, input, change = proposal runtime proposer first second
    let id = caseId owner input.CaseReference
    let draft = CaseLifecycleCandidate.draft id change
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    use auditConnection = new NpgsqlConnection(owner)
    auditConnection.Open()

    DataAudit.runWithSuppression auditConnection witness (Some commitments) cancellation
    |> await
    |> ignore

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (syntheticInventory id)
            draft
            cancellation
        |> await
    with
    | OwnerPurgeOutcome.Purged(eventId, deleted) when eventId = change.EventId && deleted > 0L -> ()
    | _ -> failtest "Synthetic owner purge did not co-commit."

    assertPurged owner id input.CaseReference "Synthetic live purge reason" commitments draft

    assertPostPurgeAudit auditConnection witness commitments

    assertOpaqueTombstone source runtime first id change commitments

    ordinaryDenied actor input
    exactRetry owner connection witness commitments id change draft

let private syntheticLivePurge =
    testCase
        "[CC-ERASE-001] owner purge removes live claimant rows but retains keyed pending proof"
        (fun _ -> CaseLifecycleStoreFixture.setup runSynthetic)

let tests = testList "case erasure live purge" [ syntheticLivePurge ]
