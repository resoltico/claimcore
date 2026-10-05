module ClaimCore.IntegrationTests.CaseErasureFenceTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.Fixtures

let private requireAccepted (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic first case must be accepted."

let private assertDenialSet
    (connection: NpgsqlConnection)
    (commitments: ISuppressionCommitments)
    caseId
    operationId
    denialSet
    =
    let operationCommitment = commitments.Operation operationId

    use denial =
        new NpgsqlCommand(
            "SELECT knowledge FROM claimcore.case_erasure_operation_denials "
            + "WHERE case_id=@case AND operation_commitment=@commitment",
            connection
        )

    denial.Parameters.AddWithValue("case", caseId) |> ignore
    denial.Parameters.AddWithValue("commitment", operationCommitment) |> ignore

    let knowledge =
        match denial.ExecuteScalar() with
        | :? string as value -> value
        | _ -> failtest "Accepted authority denial is absent."

    Expect.equal knowledge "ACCEPTED" "Accepted authority is not relabeled."

    use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
    digest.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_ERASURE_DENIAL_SET_V1\000"))
    digest.AppendData(operationCommitment)
    digest.AppendData([| 1uy |])
    Expect.sequenceEqual denialSet (digest.GetHashAndReset()) "Complete exact denial set is bound."

let private assertFence owner (witness: WitnessProtocol) (request: CommandRequest) eventId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id,phase,reference_commitment,denial_count,denial_set_sha256,"
            + "request_candidate_sha256,request_witness_sequence,request_witness_epoch,"
            + "request_witness_entry_hash FROM claimcore.case_erasure_tombstones "
            + "WHERE request_event_id=@event",
            connection
        )

    command.Parameters.AddWithValue("event", eventId) |> ignore
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Witnessed erasure fence is co-committed."
    let caseId = reader.GetGuid(0)
    Expect.equal (reader.GetString(1)) "ERASURE_REQUESTED" "Request does not claim purge."
    let referenceCommitment = reader.GetFieldValue<byte array>(2)
    Expect.equal (reader.GetInt64(3)) 1L "Accepted operation is denied."
    let denialSet = reader.GetFieldValue<byte array>(4)
    let candidate = reader.GetFieldValue<byte array>(5)
    let sequence = reader.GetInt64(6)
    let epoch = reader.GetInt64(7)
    let hash = reader.GetFieldValue<byte array>(8)
    Expect.isFalse (reader.Read()) "Exactly one erasure fence exists."
    reader.Close()

    (witness
        .VerifyAuthorityEvidence(eventId, sequence, epoch, hash, candidate, CancellationToken.None)
        .GetAwaiter()
        .GetResult())

    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

    Expect.sequenceEqual
        referenceCommitment
        (commitments.Reference request.CaseReference)
        "Reference remains keyed, not raw."

    assertDenialSet connection commitments caseId request.OperationId denialSet
    caseId

let private removeSyntheticLiveRows owner caseId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction()

    for table in
        [
            "request_preparations"
            "case_changes"
            "case_lifecycle_approvals"
            "case_lifecycle_events"
            "case_holds"
            "cases"
        ] do
        use command =
            new NpgsqlCommand(
                $"DELETE FROM claimcore.{table} WHERE case_id=@case",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("case", caseId) |> ignore
        let affected = command.ExecuteNonQuery()

        if table = "cases" then
            Expect.equal affected 1 "Only one synthetic live case is removed."

    transaction.Commit()

let private assertSuppressionSurvivesRowLoss (actor: IActorClaimsCore) request =
    let fresh = openRequest (Guid.NewGuid()) request.CaseReference

    let reused =
        openRequest request.OperationId ("OTHER-" + Guid.NewGuid().ToString("N"))

    for candidate in [ fresh; reused ] do
        match actor.Prepare(candidate, CancellationToken.None) |> await with
        | PrepareOutcome.PrepareRejected(_, Rejection.ResourceUnavailable) -> ()
        | _ -> failtest "A purged reference or operation remains unavailable."

    match actor.Get(request.CaseReference, CancellationToken.None) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "Purged reference cannot disclose existence or claimant data."

    match actor.ObserveOperation(request.OperationId, CancellationToken.None) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "Purged operation cannot disclose an accepted receipt."

let private assertRequestedListFence (actor: IActorClaimsCore) =
    match actor.List({ AfterCursor = None; Limit = 10 }, CancellationToken.None) |> await with
    | QueryOutcome.Succeeded page ->
        Expect.isEmpty page.Items "Requested erasure is hidden before list pagination."
        Expect.isNone page.NextCursor "Hidden erasure cannot influence the list cursor."
    | _ -> failtest "Authorized list remains available after an erasure request."

let private requestFence () =
    setup (fun owner (source, _) witness runtime proposer _ _ _ _ ->
        let actor = runtime.ForActor proposer

        let request =
            openRequest (Guid.NewGuid()) ("ERASURE-" + Guid.NewGuid().ToString("N"))

        requireAccepted actor request
        let current = review actor request.CaseReference
        let eventId = Guid.NewGuid()

        let change =
            change
                eventId
                request.CaseReference
                current
                (LifecycleMutation.RequestErasure "Synthetic privacy request")

        match actor.Lifecycle.Apply(change, CancellationToken.None) |> await with
        | LifecycleWriteOutcome.Applied(id, _, _) when id = eventId -> ()
        | _ -> failtest "Witnessed request must co-commit its suppression fence."

        let caseId = assertFence owner witness request eventId
        use auditConnection = RuntimeDatabase.openConnection source

        let audited =
            DataAudit.runWithSuppression
                auditConnection
                witness
                (Some(FixturePrivateFiles.syntheticCommitments witness.Identity))
                CancellationToken.None
            |> await

        Expect.equal audited.ErasureFences 1L "Full audit verified the request fence."
        assertRequestedListFence actor
        removeSyntheticLiveRows owner caseId
        assertSuppressionSurvivesRowLoss actor request)

let private assertStartupQuarantine appConnection writer =
    let outcome =
        Runtime.OpenPostgres(
            appConnection,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await

    match outcome with
    | Error RuntimeOpenFault.RuntimeStoreIntegrityError -> ()
    | _ -> failtest "Startup must quarantine an incomplete erasure fence."

let private missingDenialFailsAudit () =
    setup (fun owner (source, appConnection) witness runtime proposer _ _ _ writer ->
        let actor = runtime.ForActor proposer

        let request =
            openRequest (Guid.NewGuid()) ("ERASURE-AUDIT-" + Guid.NewGuid().ToString("N"))

        requireAccepted actor request
        let current = review actor request.CaseReference
        let eventId = Guid.NewGuid()

        let proposed =
            change
                eventId
                request.CaseReference
                current
                (LifecycleMutation.RequestErasure "Synthetic audit tamper test")

        match actor.Lifecycle.Apply(proposed, CancellationToken.None) |> await with
        | LifecycleWriteOutcome.Applied _ -> ()
        | _ -> failtest "Synthetic erasure request was not accepted."

        let caseId = assertFence owner witness request eventId
        let port = FixturePrivateFiles.syntheticCommitments witness.Identity
        use auditConnection = RuntimeDatabase.openConnection source

        DataAudit.runWithSuppression auditConnection witness (Some port) CancellationToken.None
        |> await
        |> ignore

        use ownerConnection = new NpgsqlConnection(owner)
        ownerConnection.Open()

        use remove =
            new NpgsqlCommand(
                "DELETE FROM claimcore.case_erasure_operation_denials WHERE case_id=@case",
                ownerConnection
            )

        remove.Parameters.AddWithValue("case", caseId) |> ignore
        Expect.equal (remove.ExecuteNonQuery()) 1 "One synthetic denial was removed."

        Expect.throws
            (fun () ->
                DataAudit.runWithSuppression
                    auditConnection
                    witness
                    (Some port)
                    CancellationToken.None
                |> await
                |> ignore)
            "A missing suppression denial must fail the full data audit."

        assertStartupQuarantine appConnection writer)

let tests =
    testList
        "case erasure fence"
        [
            testCase
                "[CC-ERASE-001] erasure request co-commits keyed reference and operation denials"
                requestFence
            testCase
                "[CC-ERASE-001] full audit rejects a missing erasure operation denial"
                missingDenialFailsAudit
        ]
