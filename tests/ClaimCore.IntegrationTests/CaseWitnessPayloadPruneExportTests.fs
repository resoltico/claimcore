module ClaimCore.IntegrationTests.CaseWitnessPayloadPruneExportTests

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

let private ct = CancellationToken.None

let private ownerWitness (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private proposal (review: TombstoneReview) =
    let expires = DateTimeOffset.UtcNow.AddHours(1.0)

    {
        EventId = Guid.NewGuid()
        CaseId = review.CaseId
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

let private purgeExportCase owner (witness: WitnessProtocol) caseId livePurge =
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
    | OwnerPurgeOutcome.Purged _ -> commitments
    | _ -> failtest "Export case live purge failed."

let private approvedPrune (runtime: Runtime) first second caseId =
    let steward = runtime.ForActor first

    let reviewed =
        match steward.Tombstones.Review(caseId, ct) |> await with
        | TombstoneReviewOutcome.Available value -> value
        | _ -> failtest "Export tombstone review failed."

    let request = proposal reviewed

    for principal in [ first; second ] do
        let approvalId = Guid.NewGuid()

        match
            (runtime.ForActor principal)
                .Tombstones.ApproveWitnessPrune(
                    request,
                    approvalId,
                    request.ValidUntil.AddMinutes(-1.0),
                    ct
                )
            |> await
        with
        | TombstoneWriteOutcome.Applied(id, _) when id = approvalId -> ()
        | _ -> failtest "Export prune approval failed."

    steward, request

let private assertExportLiability (connection: NpgsqlConnection) caseId =
    use copy =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copies "
            + "WHERE source_case_id=@case AND producer_kind='PRODUCT_EXPORT' AND state='UNKNOWN'",
            connection
        )

    Sql.uuid copy "case" caseId
    Expect.equal (copy.ExecuteScalar() :?> int64) 1L "Known export liability remains explicit"

let private run
    owner
    writer
    (witness: WitnessProtocol)
    (runtime: Runtime)
    _
    first
    second
    (actor: IActorClaimsCore)
    _
    livePurge
    bytes
    caseId
    =
    let commitments = purgeExportCase owner witness caseId livePurge
    let steward, request = approvedPrune runtime first second caseId
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        CaseTombstonePruneOwner.execute
            owner
            (ownerWitness writer)
            witness
            commitments
            (CaseErasurePurgeTests.syntheticInventory caseId)
            request
            ct
        |> await
    with
    | OwnerWitnessPruneOutcome.WitnessPayloadPruned(id, count) when
        id = request.EventId && count = request.TargetCount
        ->
        ()
    | _ -> failtest "Export witness prune failed."

    CaseErasureArtifactTests.purgedExportAudit owner witness commitments caseId connection
    CaseErasureArtifactTests.oldArtifactDenied actor bytes

    assertExportLiability connection caseId

    match steward.Tombstones.Review(caseId, ct) |> await with
    | TombstoneReviewOutcome.Available status when
        status.WitnessPayloadPruned
        && status.ManagedCopyCertificationPending
        && status.PrivacyPhase = ClaimCore.Domain.PrivacyPhase.ErasurePending
        ->
        ()
    | _ -> failtest "Export liability status was not pending."

let tests =
    testList
        "witness payload prune export"
        [
            testCase
                "[CC-ERASE-001] postprune audit retains unknown export liability and denies old artifact"
                (fun _ -> CaseErasureArtifactTests.withArtifact run)
        ]
