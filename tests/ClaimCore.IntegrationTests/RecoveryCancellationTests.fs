module ClaimCore.IntegrationTests.RecoveryCancellationTests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RecoveryCancellationFixture

let private cancelled () =
    let cancellation = new CancellationTokenSource()
    cancellation.Cancel()
    cancellation

let private retained (recovery: IRecoveryStore) material =
    match recovery.Retain(material, CancellationToken.None) |> await with
    | Ok(RecoveryRetain.Created value)
    | Ok(RecoveryRetain.Existing value) -> value
    | Ok _ -> failtest "Synthetic technical preparation must not observe terminal authority."
    | Error _ -> failtest "Synthetic technical preparation must be retained."

let private read (recovery: IRecoveryStore) operationId =
    match recovery.Get(operationId, CancellationToken.None) |> await with
    | Ok value -> value
    | Error _ -> failtest "Synthetic preparation must be readable."

let private inspect (recovery: IRecoveryStore) operationId =
    match
        recovery.Inspect(
            operationId,
            None,
            PreparationLimits.defaults.MaximumPageSize,
            CancellationToken.None
        )
        |> await
    with
    | Ok(Some(RecoveryStoreInspection.Retained(header, attempts, authority))) ->
        header, attempts, authority
    | _ -> failtest "Synthetic preparation evidence must be readable."

let private retainCancellation =
    testCase "[CC-REC-001] cancelled retain has no durable preparation" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())
        let material, context = draft source
        let port = recovery source context
        use cancellation = cancelled ()

        match port.Retain(material, cancellation.Token) |> await with
        | Error RecoveryStoreFailure.CancelledBeforeCommit -> ()
        | _ -> failtest "Pre-commit preparation cancellation must be definite."

        Expect.isNone (read port material.OperationId) "Cancelled retain wrote no row"
        retained port material |> ignore)

let private attemptCancellation =
    testCase "[CC-REC-001] cancelled attempt admission creates no marker or attempt" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())
        let material, context = draft source
        let preparer = recovery source context
        retained preparer material |> ignore
        let port = operationPort source EndpointAction.RecoveryResolve material.OperationId
        use cancellation = cancelled ()

        match port.Start(material.OperationId, cancellation.Token) |> await with
        | Error RecoveryStoreFailure.CancelledBeforeCommit -> ()
        | _ -> failtest "Pre-commit attempt cancellation must be definite."

        let header, attempts, authority = inspect port material.OperationId
        Expect.equal header.Lifecycle PreparationLifecycle.Unsubmitted "No start marker"
        Expect.equal authority RecoveryAuthority.PendingAuthority "Pending authority remains"
        Expect.isEmpty attempts.Items "No attempt row")

let private dismissalCancellation =
    testCase "[CC-REC-001] cancelled dismissal leaves preparation actionable" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())
        let material, context = draft source
        let preparer = recovery source context
        retained preparer material |> ignore
        let port = operationPort source EndpointAction.RecoveryDismiss material.OperationId
        use cancellation = cancelled ()

        match
            port.Dismiss(material.OperationId, material.RequestSha256, cancellation.Token)
            |> await
        with
        | Error RecoveryStoreFailure.CancelledBeforeCommit -> ()
        | _ -> failtest "Pre-commit dismissal cancellation must be definite."

        let header, _, authority = inspect port material.OperationId
        Expect.equal header.Lifecycle PreparationLifecycle.Unsubmitted "No dismissal marker"
        Expect.equal authority RecoveryAuthority.PendingAuthority "Pending authority remains")

let private settlementCancellation =
    testCase "[CC-REC-001] cancelled settlement leaves admitted attempt unsettled" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())
        let material, context = draft source
        let preparer = recovery source context
        retained preparer material |> ignore
        let port = operationPort source EndpointAction.RecoveryResolve material.OperationId

        let attemptId =
            match port.Start(material.OperationId, CancellationToken.None) |> await with
            | Ok(RecoveryStart.Started(id, _)) -> id
            | _ -> failtest "Synthetic attempt must be admitted."

        use cancellation = cancelled ()

        match port.Settle(attemptId, RecoverySettlement.Accepted, cancellation.Token) |> await with
        | Error RecoveryStoreFailure.CancelledBeforeCommit -> ()
        | _ -> failtest "Pre-commit settlement cancellation must be definite."

        let _, attempts, _ = inspect port material.OperationId

        match attempts.Items with
        | [ attempt ] ->
            Expect.equal attempt.AttemptId attemptId "Admitted attempt remains"
            Expect.isNone attempt.Settlement "No synthetic settlement"
        | _ -> failtest "Exactly one unsettled attempt remains.")

let private cancellationAtCommitBoundary =
    testCase
        "[CC-REC-001] cancellation immediately before technical commit rolls back synthetic marker"
        (fun () ->
            use source = NpgsqlDataSource.Create(appConnection ())
            use connection = RuntimeDatabase.openConnection source
            use cancellation = new CancellationTokenSource()
            let commitStarted = ref false

            let operation =
                PreparationData.withTransaction
                    connection
                    cancellation.Token
                    commitStarted
                    (fun transaction ->
                        task {
                            use marker =
                                new NpgsqlCommand(
                                    "CREATE TEMP TABLE claimcore_precommit_rollback(token uuid)",
                                    connection,
                                    transaction
                                )

                            let! _ = marker.ExecuteNonQueryAsync(cancellation.Token)
                            cancellation.Cancel()
                            return ()
                        })

            Expect.throws
                (fun () -> operation.GetAwaiter().GetResult())
                "Pre-commit cancellation must fail the transaction"

            Expect.isFalse commitStarted.Value "Commit boundary was not crossed"

            use query =
                new NpgsqlCommand(
                    "SELECT to_regclass('pg_temp.claimcore_precommit_rollback') IS NULL",
                    connection
                )

            Expect.isTrue
                (query.ExecuteScalar() :?> bool)
                "Synthetic transaction-local marker was rolled back")

let private cancelledReads =
    testCase
        "[CC-REC-001] cancelled recovery reads return cancellation without disclosure"
        (fun () ->
            use source = NpgsqlDataSource.Create(appConnection ())
            let material, context = draft source
            let port = recovery source context
            use cancellation = cancelled ()

            match port.Get(material.OperationId, cancellation.Token) |> await with
            | Error RecoveryStoreFailure.ReadCancelled -> ()
            | _ -> failtest "Cancelled detail read must remain cancellation."

            let listContext =
                (gate source)
                    .Installation(
                        ActorBoundStoreFixture.actorPrincipal (),
                        EndpointAction.RecoveryList,
                        CancellationToken.None
                    )
                |> await
                |> Option.defaultWith (fun () ->
                    failtest "Synthetic recovery list was not admitted.")

            let listing = recovery source listContext

            match listing.List(RecoveryListView.Pending, None, 1, cancellation.Token) |> await with
            | Error RecoveryStoreFailure.ReadCancelled -> ()
            | _ -> failtest "Cancelled page read must remain cancellation."

            match port.InstallationLineage cancellation.Token |> await with
            | Error RecoveryStoreFailure.ReadCancelled -> ()
            | _ -> failtest "Cancelled lineage read must remain cancellation.")

let private commitStartFailure =
    testCase "[CC-REC-001] technical commit-start failure remains uncertain" (fun () ->
        use cancellation = new CancellationTokenSource()
        let commitStarted = ref false

        let operation =
            PreparationData.commitBoundary cancellation.Token commitStarted (fun () ->
                Task.FromException(TimeoutException()))

        Expect.throws
            (fun () -> operation.GetAwaiter().GetResult())
            "Injected failure crossed the exact production commit boundary"

        Expect.isTrue commitStarted.Value "Commit boundary flag set before provider call"

        Expect.equal
            (PreparationData.mutationFailure commitStarted.Value (TimeoutException()))
            RecoveryStoreFailure.TechnicalMutationUnknown
            "No inferred rollback after commit start")

let private cancellationAfterCommitStart =
    testCase "[CC-REC-001] caller cancellation after commit start cannot relabel outcome" (fun () ->
        use cancellation = new CancellationTokenSource()
        let commitStarted = ref false

        let operation =
            PreparationData.commitBoundary cancellation.Token commitStarted (fun () ->
                cancellation.Cancel()
                Task.CompletedTask)

        operation.GetAwaiter().GetResult()

        Expect.isTrue commitStarted.Value "Commit boundary was crossed"
        Expect.isTrue cancellation.IsCancellationRequested "Caller canceled after boundary")

let tests =
    testList
        "PostgreSQL recovery cancellation"
        [
            retainCancellation
            attemptCancellation
            dismissalCancellation
            settlementCancellation
            cancellationAtCommitBoundary
            cancelledReads
            commitStartFailure
            cancellationAfterCommitStart
        ]
