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
    witnessedOpen (appConnection ()) CancellationToken.None
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic lifecycle runtime must open.")

let private prepared (core: IActorClaimsCore) operationId reference =
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

let private recovery operationId =
    let source = NpgsqlDataSource.Create(appConnection ())
    let witness = witnessProtocol ()

    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let context =
        gate.Operation(
            ActorBoundStoreFixture.actorPrincipal (),
            EndpointAction.RecoveryResolve,
            operationId,
            CancellationToken.None
        )
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic recovery actor was not admitted.")

    source,
    (PostgresRecoveryStore(source, PreparationLimits.defaults, witness, context) :> IRecoveryStore)

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
            clock.Capture,
            (fun today current -> Claim.decide today request current),
            CancellationToken.None
        )
        |> await
    with
    | Ok(AdmittedExecution.RevokedBeforeExecution _) -> ()
    | _ -> failtest message

let private assertNoCase (core: IActorClaimsCore) request =
    match core.Get(request.CaseReference, CancellationToken.None) |> await with
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "An absent case must use the non-disclosing public refusal."

    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM claimcore.cases WHERE case_reference=@reference)",
            connection
        )

    Sql.text command "reference" request.CaseReference
    Expect.isFalse (command.ExecuteScalar() :?> bool) "Revocation before execution leaves no case."

let private pruneRevoked operationId =
    ageRevokedPreparation operationId

    let result =
        PreparationPruning.prune
            (adminConnection ())
            { PreparationPruneOptions.defaults with
                AbandonedRetentionDays = 1
            }
        |> completedAdministration

    Expect.equal result.DeletedCount 1 "The aged terminal preparation is owner-prunable"

let private assertTombstone (core: IActorClaimsCore) operationId =
    match core.Recovery.Inspect(operationId, None, 8, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RevokedInspection tombstone)) ->
        Expect.equal tombstone.OperationId operationId "Tombstone preserves only terminal identity"
    | _ -> failtest "Pruned revocation must remain inspectable without request payload."

let private assertExactRevocation (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.RejectedBeforeAttempt(_, Rejection.ResourceUnavailable) -> ()
    | _ -> failtest "A pruned revocation must refuse exact retry without disclosing identity."

let private startThenRevokeThenPrune =
    testCase
        "[CC-REC-001] a started worker cannot execute after revocation or later preparation pruning"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()

            let request, digest, operation =
                prepared (actorCore runtime) operationId ("REVOKED-" + operationId.ToString("N"))

            let source, port = recovery operationId
            use source = source

            let attemptId =
                match port.Start(operationId, CancellationToken.None) |> await with
                | Ok(RecoveryStart.Started(attemptId, _)) -> attemptId
                | _ -> failtest "A single synthetic worker must receive one admitted attempt."

            match
                (actorCore runtime)
                    .Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
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

            assertNoCase (actorCore runtime) request
            pruneRevoked operationId

            assertRevokedExecution
                port
                request
                operation
                attemptId
                "A delayed worker remains revoked after the optional preparation is gone."

            assertTombstone (actorCore runtime) operationId
            assertExactRevocation (actorCore runtime) request)

let private attemptsUntilLimit (port: IRecoveryStore) operationId =
    [ 1..64 ]
    |> List.map (fun _ -> port.Start(operationId, CancellationToken.None) |> await)
    |> List.iter (function
        | Ok(RecoveryStart.Started _)
        | Ok(RecoveryStart.AlreadyStarted _) -> ()
        | _ -> failtest "Every attempt through the configured limit must be admitted.")

let private assertAttemptLimit
    (core: IActorClaimsCore)
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

let private attemptPage (core: IActorClaimsCore) operationId cursor =
    match core.Recovery.Inspect(operationId, cursor, 16, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection value)) ->
        value.Preparation.Attempts
    | _ -> failtest "Bounded attempt evidence page must be readable."

let private assertKeysetPages (core: IActorClaimsCore) operationId =
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

let private assertTerminalAccess (core: IActorClaimsCore) operationId digest =
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
                prepared (actorCore runtime) operationId ("ATTEMPTS-" + operationId.ToString("N"))

            let source, port = recovery operationId
            use source = source
            attemptsUntilLimit port operationId
            assertAttemptLimit (actorCore runtime) request digest port operationId
            assertKeysetPages (actorCore runtime) operationId
            assertTerminalAccess (actorCore runtime) operationId digest)

let tests =
    testList
        "PostgreSQL operation authority lifecycle"
        [ startThenRevokeThenPrune; attemptLimitAndPaging ]
