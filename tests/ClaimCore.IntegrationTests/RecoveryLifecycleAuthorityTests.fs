module ClaimCore.IntegrationTests.RecoveryLifecycleAuthorityTests

open System
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic lifecycle runtime must open.")

let private prepared (core: IClaimsCore) operationId reference =
    let request = openRequest operationId reference

    let digest =
        match core.Prepare(request, CancellationToken.None) |> await with
        | PrepareOutcome.Prepared(details, _) ->
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () ->
                failtest "Prepared recovery identity requires a digest.")
        | _ -> failtest "Synthetic request must retain before lifecycle qualification."

    let operation =
        Operation.prepare request
        |> Result.defaultWith (fun _ ->
            failtest "Synthetic closed request must prepare internally.")

    request, digest, operation

let private recovery () =
    let source = NpgsqlDataSource.Create(appConnection ())
    source, (PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore)

let private ageRevokedPreparation operationId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.operation_revocations "
            + "SET revoked_at = clock_timestamp() - interval '3 days' WHERE operation_id = @operation; "
            + "UPDATE claimcore.request_preparations "
            + "SET prepared_at = clock_timestamp() - interval '3 days' WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Expect.equal (command.ExecuteNonQuery()) 2 "Only synthetic terminal authority is aged"

let private assertRevokedExecution
    (port: IRecoveryStore)
    (request: CommandRequest)
    (operation: PreparedOperation)
    (attemptId: Guid)
    (message: string)
    =
    match
        port.ExecuteAdmitted(
            operation,
            attemptId,
            (fun () -> clock.Capture().EffectiveBusinessDate),
            (fun today current -> Claim.decide today request current),
            CancellationToken.None
        )
        |> await
    with
    | Ok(AdmittedExecution.RevokedBeforeExecution _) -> ()
    | _ -> failtest message

let private assertNoCase (core: IClaimsCore) request =
    match core.Get(request.CaseReference, CancellationToken.None) |> await with
    | QueryOutcome.Succeeded(Lookup.NotFound _) -> ()
    | _ -> failtest "Revocation before execution must leave the case absent."

let private pruneRevoked operationId =
    ageRevokedPreparation operationId

    let result =
        PreparationPruning.prune
            (adminConnection ())
            { PreparationPruneOptions.defaults with
                AbandonedRetentionDays = 1
            }

    Expect.equal result.DeletedCount 1 "The aged terminal preparation is owner-prunable"

let private assertTombstone (core: IClaimsCore) operationId =
    match core.Recovery.Inspect(operationId, None, 8, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RevokedInspection tombstone)) ->
        Expect.equal tombstone.OperationId operationId "Tombstone preserves only terminal identity"
    | _ -> failtest "Pruned revocation must remain inspectable without request payload."

let private assertExactRevocation (core: IClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
        Expect.equal rejection.Code RejectionCode.OperationRevoked "Exact retry remains terminal"
    | _ -> failtest "The pruned revocation must still refuse the exact native request."

let private startThenRevokeThenPrune =
    testCase
        "[CC-REC-001] a started worker cannot execute after revocation or later preparation pruning"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()

            let request, digest, operation =
                prepared runtime.Core operationId ("REVOKED-" + operationId.ToString("N"))

            let source, port = recovery ()
            use source = source

            let attemptId =
                match port.Start(operationId, CancellationToken.None) |> await with
                | Ok(RecoveryStart.Started(attemptId, _)) -> attemptId
                | _ -> failtest "A single synthetic worker must receive one admitted attempt."

            match
                runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
                |> await
            with
            | RecoveryDismissOutcome.DismissedPreparation _ -> ()
            | _ -> failtest "Dismissal must close authority even after Start committed."

            assertRevokedExecution
                port
                request
                operation
                attemptId
                "The delayed worker must settle as revoked without a business write."

            assertNoCase runtime.Core request
            pruneRevoked operationId

            assertRevokedExecution
                port
                request
                operation
                attemptId
                "A delayed worker remains revoked after the optional preparation is gone."

            assertTombstone runtime.Core operationId
            assertExactRevocation runtime.Core request)

let private attemptsUntilLimit (port: IRecoveryStore) operationId =
    [ 1..64 ]
    |> List.map (fun _ -> port.Start(operationId, CancellationToken.None) |> await)
    |> List.iter (function
        | Ok(RecoveryStart.Started _)
        | Ok(RecoveryStart.AlreadyStarted _) -> ()
        | _ -> failtest "Every attempt through the configured limit must be admitted.")

let private assertAttemptLimit
    (core: IClaimsCore)
    (request: CommandRequest)
    digest
    (port: IRecoveryStore)
    operationId
    =
    match port.Start(operationId, CancellationToken.None) |> await with
    | Error RecoveryStoreFailure.CapacityExceeded -> ()
    | _ -> failtest "The sixty-fifth identified attempt must be rejected at storage admission."

    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.RefusedBeforeAttempt(_, rejection) ->
        Expect.equal
            rejection.Code
            RecoveryRejectionCode.AttemptLimitReached
            "Recovery exposes the attempt limit as an actionable terminal refusal"
    | _ -> failtest "The public recovery boundary must not disguise attempt exhaustion as a fault."

    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.RejectedBeforeAttempt(_, rejection) ->
        Expect.equal
            rejection.Code
            RejectionCode.RecoveryAttemptLimitReached
            "Normal exact retry exposes the same core-owned attempt limit"
    | _ -> failtest "The native retry boundary must preserve actionable attempt exhaustion."

let private attemptPage (core: IClaimsCore) operationId cursor =
    match core.Recovery.Inspect(operationId, cursor, 16, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection value)) ->
        value.Preparation.Attempts
    | _ -> failtest "Bounded attempt evidence page must be readable."

let private assertKeysetPages (core: IClaimsCore) operationId =
    let first = attemptPage core operationId None
    Expect.equal first.Items.Length 16 "Inspection obeys the requested page limit"

    let cursor =
        first.NextCursor
        |> Option.defaultWith (fun () ->
            failtest "More than one attempt page must produce a cursor.")

    let second = attemptPage core operationId (Some cursor)
    let firstIds = first.Items |> List.map _.AttemptId |> Set.ofList
    let secondIds = second.Items |> List.map _.AttemptId |> Set.ofList
    Expect.isEmpty (Set.intersect firstIds secondIds) "Keyset pages do not duplicate attempts"

let private assertTerminalAccess (core: IClaimsCore) operationId digest =
    match
        core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
    | _ -> failtest "Attempt admission limits cannot block exact export."

    match
        core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
        |> await
    with
    | RecoveryDismissOutcome.DismissedPreparation _ -> ()
    | _ -> failtest "Attempt admission limits cannot block durable revocation."

let private attemptLimitAndPaging =
    testCase
        "[CC-REC-001] attempt 65 is refused while bounded evidence pages remain available"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()

            let request, digest, _ =
                prepared runtime.Core operationId ("ATTEMPTS-" + operationId.ToString("N"))

            let source, port = recovery ()
            use source = source
            attemptsUntilLimit port operationId
            assertAttemptLimit runtime.Core request digest port operationId
            assertKeysetPages runtime.Core operationId
            assertTerminalAccess runtime.Core operationId digest)

let tests =
    testList
        "PostgreSQL operation authority lifecycle"
        [ startThenRevokeThenPrune; attemptLimitAndPaging ]
