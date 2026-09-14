module ClaimCore.IntegrationTests.RuntimeLifecycleTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private exclusiveLock table =
    let connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    let transaction = connection.BeginTransaction()

    use command =
        new NpgsqlCommand(
            $"LOCK TABLE claimcore.{table} IN ACCESS EXCLUSIVE MODE",
            connection,
            transaction
        )

    command.ExecuteNonQuery() |> ignore
    connection, transaction

let private admittedQuerySurvivesDispose () =
    use runtime =
        Runtime.OpenPostgres(appConnection (), CancellationToken.None)
        |> await
        |> accepted

    let core = runtime.Core
    let connection, lockedTransaction = exclusiveLock "cases"
    use connection = connection
    use transaction = lockedTransaction

    let pending =
        core.Get("RUNTIME-DRAIN-" + Guid.NewGuid().ToString("N"), CancellationToken.None)

    Task.Delay(100).GetAwaiter().GetResult()
    Expect.isFalse pending.IsCompleted "The database lock keeps an admitted query in flight"
    let disposing = Task.Run(fun () -> (runtime :> IDisposable).Dispose())

    try
        Expect.isTrue
            (SpinWait.SpinUntil(
                (fun () ->
                    try
                        core.Describe() |> ignore
                        false
                    with :? ObjectDisposedException ->
                        true),
                2000
            ))
            "Disposal closes admission before the query finishes"

        Expect.isFalse disposing.IsCompleted "The admitted query still owns the source"
        transaction.Commit()

        match pending |> await with
        | QueryOutcome.Succeeded(Lookup.NotFound _) -> ()
        | _ -> failtest "Disposal must not relabel an admitted query outcome"

        Expect.isTrue (disposing.Wait(2000)) "The runtime drains and disposes after completion"
    finally
        if not disposing.IsCompleted then
            disposing.Wait(2000) |> ignore

let private cancellationInterruptsSchemaInspection () =
    let connection, lockedTransaction = exclusiveLock "schema_migrations"
    use connection = connection
    use transaction = lockedTransaction
    use cancellation = new CancellationTokenSource()
    let opening = Runtime.OpenPostgres(appConnection (), cancellation.Token)
    Task.Delay(100).GetAwaiter().GetResult()
    Expect.isFalse opening.IsCompleted "Schema inspection must wait behind the test lock"
    cancellation.Cancel()

    match opening |> await with
    | Error RuntimeOpenFault.RuntimeCancelled -> ()
    | Ok runtime ->
        use unexpected = runtime
        failtest "A cancelled opening must never return a live runtime"
    | Error _ -> failtest "Mid-open cancellation must retain its own safe fault"

    transaction.Commit()

let private openingRequest () =
    openRequest (Guid.NewGuid()) ("RUNTIME-MUTATION-" + Guid.NewGuid().ToString("N"))

let private admittedMutationSurvivesDispose () =
    use runtime =
        Runtime.OpenPostgres(appConnection (), CancellationToken.None)
        |> await
        |> accepted

    let core = runtime.Core
    let connection, lockedTransaction = exclusiveLock "request_preparations"
    use connection = connection
    use transaction = lockedTransaction
    let pending = core.Execute(openingRequest (), CancellationToken.None)
    Task.Delay(100).GetAwaiter().GetResult()
    Expect.isFalse pending.IsCompleted "The test lock keeps an admitted mutation in flight"
    let disposing = Task.Run(fun () -> (runtime :> IDisposable).Dispose())

    try
        Expect.isTrue
            (SpinWait.SpinUntil(
                (fun () ->
                    try
                        core.Describe() |> ignore
                        false
                    with :? ObjectDisposedException ->
                        true),
                2000
            ))
            "The runtime closes admission during a mutation"

        transaction.Commit()

        match pending |> await with
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
        | _ -> failtest "Disposal must not relabel an admitted accepted mutation"

        Expect.isTrue (disposing.Wait(10000)) "Disposal completes after mutation settlement"
    finally
        if not disposing.IsCompleted then
            disposing.Wait(2000) |> ignore

let private thrownOpeningClosesOwnedSource () =
    let mutable disposed = 0
    let sentinel = "synthetic-sensitive-provider-detail"

    let outcome =
        RuntimeSourceOwnership.openOwned
            (fun () ->
                { new IDisposable with
                    member _.Dispose() =
                        Interlocked.Increment(&disposed) |> ignore
                })
            (fun _ ->
                Task.FromException<Result<int, RuntimeOpenFault>>(InvalidDataException(sentinel)))
            (fun _ source -> source)
            (fun _ -> RuntimeOpenFault.RuntimeStoreUnavailable)
            CancellationToken.None
        |> await

    match outcome with
    | Error fault ->
        Expect.equal fault RuntimeOpenFault.RuntimeStoreUnavailable "Only the safe fault escapes"
        Expect.isFalse (fault.ToString().Contains(sentinel)) "Provider details stay private"
    | Ok _ -> failtest "A throwing opener cannot transfer the source"

    Expect.equal disposed 1 "The opener closes its source on an unexpected exception"

let tests =
    testList
        "runtime lifecycle under PostgreSQL"
        [
            testCase
                "[CC-RUN-001] admitted query keeps its typed outcome while runtime drains"
                admittedQuerySurvivesDispose
            testCase
                "[CC-RUN-001] mid-open cancellation interrupts schema inspection"
                cancellationInterruptsSchemaInspection
            testCase
                "[CC-RUN-001] admitted mutation remains accepted while runtime drains"
                admittedMutationSurvivesDispose
            testCase
                "[CC-RUN-001] thrown opening closes its owned source"
                thrownOpeningClosesOwnedSource
        ]
