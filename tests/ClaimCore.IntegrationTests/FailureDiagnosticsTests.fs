module ClaimCore.IntegrationTests.FailureDiagnosticsTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open Npgsql
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testSequenced
    <| testList
        "safe storage failure diagnostics"
        [
            testTask "cleanup and throwing observers preserve a confirmed transaction result" {
                do!
                    task {
                        let! created = PostgresFixture.create ()
                        let database = created

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = database.Fixture.Dispose()
                            }

                        use activity =
                            new Diagnostics.Activity("ClaimCore.Storage.DiagnosticControl")

                        activity.Start() |> ignore
                        let observed = Collections.Concurrent.ConcurrentQueue<FailureDiagnostic>()

                        use _subscription =
                            FailureDiagnostics.listener.Subscribe(
                                { new IObserver<Collections.Generic.KeyValuePair<string, obj>> with
                                    member _.OnNext value =
                                        match Diagnostics.Activity.Current with
                                        | null -> ()
                                        | current when current.TraceId = activity.TraceId ->
                                            observed.Enqueue(unbox value.Value)
                                        | _ -> ()

                                        raise (
                                            InvalidOperationException "synthetic observer secret"
                                        )

                                    member _.OnError _ = ()
                                    member _.OnCompleted() = ()
                                }
                            )

                        let mutable writerCalled = false

                        use writer =
                            { new IO.TextWriter() with
                                override _.Encoding = Text.Encoding.UTF8

                                override _.WriteLine(_: string | null) : unit =
                                    writerCalled <- true
                                    raise (IO.IOException "synthetic writer secret")
                            }

                        use _sink = ClaimCore.Server.StorageFailureLog.attach writer

                        use _diagnostic = FailureDiagnostics.scope FailurePhase.Commit

                        let! result =
                            DatabaseSession.transaction
                                database.OwnerSource
                                Data.IsolationLevel.ReadCommitted
                                CancellationToken.None
                                (fun connection transaction ->
                                    task {
                                        let! _ =
                                            DatabaseSession.execute
                                                connection
                                                "CREATE TABLE public.diagnostic_commit(value integer)"
                                                [||]
                                                CancellationToken.None

                                        connection.StateChange.Add(fun change ->
                                            if
                                                change.CurrentState = Data.ConnectionState.Closed
                                            then
                                                let error =
                                                    InvalidOperationException
                                                        "synthetic credential SQL payload secret"

                                                error.Data["provider-detail"] <-
                                                    "synthetic principal reference and token secret"

                                                raise error)

                                        do! transaction.CommitAsync()
                                        return 42
                                    })

                        Expect.equal
                            result
                            42
                            "Confirmed result survives observer and cleanup failures"

                        Expect.isTrue
                            writerCalled
                            "The actual production writer failed without changing confirmed success"

                        Expect.equal observed.Count 1 "Cleanup remains diagnosable"
                        let evidence = observed.ToArray()[0]
                        Expect.equal evidence.Phase FailurePhase.Cleanup "Safe phase"

                        Expect.equal
                            evidence.Classification
                            FailureClass.Internal
                            "Safe classification"

                        Expect.isLessThanOrEqual evidence.Methods.Length 8 "Bounded methods"
                        Expect.notEqual evidence.Correlation Guid.Empty "Internal correlation"
                        let serialized = sprintf "%A" evidence

                        Expect.stringContains
                            serialized
                            (evidence.Correlation.ToString("D"))
                            "The safe diagnostic carries usable correlation evidence"

                        Expect.isFalse
                            (serialized.Contains("secret", StringComparison.Ordinal))
                            "No exception message"

                        let! rows =
                            DatabaseSession.read
                                database.OwnerSource
                                CancellationToken.None
                                (fun connection ->
                                    DatabaseSession.rows
                                        connection
                                        "SELECT count(*) FROM public.diagnostic_commit"
                                        [||]
                                        CancellationToken.None)

                        Expect.hasLength rows 1 "Next request works after cleanup diagnostics"
                    }
            }
            testTask "diagnostics bound known failure classes without exception data" {
                do!
                    task {
                        use activity =
                            new Diagnostics.Activity("ClaimCore.Storage.DiagnosticControl")

                        activity.Start() |> ignore
                        let observed = Collections.Concurrent.ConcurrentQueue<FailureDiagnostic>()

                        use _subscription =
                            FailureDiagnostics.listener.Subscribe(
                                { new IObserver<Collections.Generic.KeyValuePair<string, obj>> with
                                    member _.OnNext value =
                                        match Diagnostics.Activity.Current with
                                        | null -> ()
                                        | current when current.TraceId = activity.TraceId ->
                                            observed.Enqueue(unbox value.Value)
                                        | _ -> ()

                                    member _.OnError _ = ()
                                    member _.OnCompleted() = ()
                                }
                            )

                        use _request = StorageDiagnostics.correlate (Guid.NewGuid())

                        for phase, error in
                            [
                                FailurePhase.Open,
                                (IO.IOException "synthetic SQL and principal" :> exn)
                                FailurePhase.Append, (TimeoutException "synthetic token" :> exn)
                                FailurePhase.Commit,
                                (OperationCanceledException "synthetic payload" :> exn)
                            ] do
                            use _attempt = FailureDiagnostics.scope phase
                            FailureDiagnostics.report error

                        Expect.equal
                            (observed |> Seq.map _.Phase |> Seq.toList)
                            [ FailurePhase.Open; FailurePhase.Append; FailurePhase.Commit ]
                            "Failure context before broad mapping"

                        Expect.equal
                            (observed |> Seq.map _.Classification |> Seq.toList)
                            [
                                FailureClass.InputOutput
                                FailureClass.Timeout
                                FailureClass.Cancelled
                            ]
                            "Finite classifications"

                        Expect.equal
                            (observed |> Seq.map _.Correlation |> Seq.distinct |> Seq.length)
                            3
                            "Distinct storage attempt identities"

                        Expect.equal
                            (observed |> Seq.map _.RequestCorrelation |> Seq.distinct |> Seq.length)
                            1
                            "One internally generated request correlation across attempts"
                    }
            }
            testTask "startup sessions and housekeeping report real provider failures once" {
                do!
                    task {
                        let! database = PostgresFixture.create ()

                        try
                            let settings =
                                NpgsqlConnectionStringBuilder(
                                    database.RuntimeSource.ConnectionString
                                )

                            settings.Password <- "synthetic-only"

                            let! admitted =
                                Runtime.openConnection
                                    settings.ConnectionString
                                    CancellationToken.None

                            use runtime =
                                admitted
                                |> Result.defaultWith (fun fault -> failtestf "Runtime: %A" fault)

                            let correlation = Guid.NewGuid()

                            let observed =
                                Collections.Concurrent.ConcurrentQueue<FailureDiagnostic>()

                            use _subscription =
                                StorageDiagnostics.subscribe (fun evidence ->
                                    if evidence.RequestCorrelation = Some correlation then
                                        observed.Enqueue evidence)

                            use _request = StorageDiagnostics.correlate correlation
                            use! owner = database.OwnerSource.OpenConnectionAsync()

                            let! _ =
                                DatabaseSession.execute
                                    owner
                                    "REVOKE SELECT, DELETE ON claimcore.sessions FROM claimcore_runtime"
                                    [||]
                                    CancellationToken.None

                            let fails (action: unit -> System.Threading.Tasks.Task<unit>) =
                                task {
                                    let mutable provider = false

                                    try
                                        do! action ()
                                    with :? NpgsqlException ->
                                        provider <- true

                                    Expect.isTrue
                                        provider
                                        "Owned permission fault reaches the actual storage owner"
                                }

                            do!
                                fails (fun () ->
                                    task {
                                        let! _ =
                                            runtime.Sessions.Retrieve(
                                                String.replicate 64 "a",
                                                CancellationToken.None
                                            ) in

                                        return ()
                                    })

                            do!
                                fails (fun () ->
                                    task {
                                        let! _ =
                                            runtime.Housekeep(
                                                (fun instant ->
                                                    System.DateOnly.FromDateTime
                                                        instant.UtcDateTime),
                                                CancellationToken.None
                                            ) in

                                        return ()
                                    })

                            settings.Password <- "private-startup-secret"

                            let! startup =
                                Runtime.openConnection
                                    settings.ConnectionString
                                    CancellationToken.None

                            Expect.isError startup "The invalid credential cannot admit a runtime"
                            let entries = observed.ToArray()

                            Expect.equal
                                (entries |> Array.map _.Phase |> Array.toList)
                                [
                                    FailurePhase.Session
                                    FailurePhase.Housekeeping
                                    FailurePhase.Open
                                ]
                                "One primary event per failed owner, before public mapping"

                            Expect.isTrue
                                (entries
                                 |> Array.forall (fun value ->
                                     value.Classification = FailureClass.Provider))
                                "Original provider classification"

                            Expect.equal
                                (entries
                                 |> Array.map _.Correlation
                                 |> Array.distinct
                                 |> Array.length)
                                3
                                "Each actual attempt has its own identity"

                            Expect.isFalse
                                ((sprintf "%A" entries).Contains "private-startup-secret")
                                "No credential in diagnostic values"
                        finally
                            database.Fixture.Dispose()
                    }
            }
            testCase "production sink disposal removes subscriptions exactly once" (fun () ->
                let correlation = Guid.NewGuid()
                use _request = StorageDiagnostics.correlate correlation
                use text = new IO.StringWriter()
                use writer = IO.TextWriter.Synchronized text

                for _ in 1..3 do
                    let sink = ClaimCore.Server.StorageFailureLog.attach writer

                    do
                        use _attempt = FailureDiagnostics.scope FailurePhase.Read
                        FailureDiagnostics.report (IO.IOException "private unsubscribed canary")

                    sink.Dispose()
                    sink.Dispose()
                    use _after = FailureDiagnostics.scope FailurePhase.Read
                    FailureDiagnostics.report (IO.IOException "private unsubscribed canary")

                let lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)

                let matching =
                    lines |> Array.filter (fun line -> line.Contains(correlation.ToString("D")))

                Expect.hasLength
                    matching
                    3
                    "Each active sink receives once and no disposed sink receives later attempts"

                Expect.isFalse
                    (String.concat "" matching |> fun value -> value.Contains "canary")
                    "No raw exception text")
            testTask "known pre-COMMIT cancellation survives failing cleanup and rolls back" {
                do!
                    task {
                        let! database = PostgresFixture.create ()

                        try
                            let correlation = Guid.NewGuid()

                            let entries =
                                Collections.Concurrent.ConcurrentQueue<FailureDiagnostic>()

                            use _subscription =
                                StorageDiagnostics.subscribe (fun value ->
                                    if value.RequestCorrelation = Some correlation then
                                        entries.Enqueue value)

                            use _request = StorageDiagnostics.correlate correlation
                            use stop = new CancellationTokenSource()
                            let mutable cancelled = false

                            try
                                let! _ =
                                    DatabaseSession.transaction
                                        database.OwnerSource
                                        Data.IsolationLevel.ReadCommitted
                                        stop.Token
                                        (fun connection transaction ->
                                            task {
                                                FailureDiagnostics.phase FailurePhase.Append

                                                let! _ =
                                                    DatabaseSession.execute
                                                        connection
                                                        "CREATE TABLE public.cancelled_cleanup(value integer)"
                                                        [||]
                                                        stop.Token

                                                connection.StateChange.Add(fun change ->
                                                    if
                                                        change.CurrentState =
                                                            Data.ConnectionState.Closed
                                                    then
                                                        raise (
                                                            IO.IOException
                                                                "private cleanup secret"
                                                        ))

                                                stop.Cancel()
                                                do! DatabaseSession.commit transaction stop.Token
                                                return 42
                                            })

                                ()
                            with :? OperationCanceledException ->
                                cancelled <- true

                            Expect.isTrue
                                cancelled
                                "Cleanup failure cannot wrap definite pre-dispatch cancellation as uncertainty"

                            let observed = entries.ToArray()

                            Expect.equal
                                (observed
                                 |> Array.map (fun value -> value.Phase, value.Classification)
                                 |> Array.toList)
                                [
                                    FailurePhase.Append, FailureClass.Cancelled
                                    FailurePhase.Cleanup, FailureClass.InputOutput
                                ]
                                "One original cancellation and distinct cleanup event"

                            let! rows =
                                DatabaseSession.read
                                    database.OwnerSource
                                    CancellationToken.None
                                    (fun connection ->
                                        DatabaseSession.rows
                                            connection
                                            "SELECT to_regclass('public.cancelled_cleanup')::text"
                                            [||]
                                            CancellationToken.None)

                            Expect.equal
                                (DatabaseSession.optional<string> 0 rows.Head)
                                None
                                "Awaited rollback left no mutation and an independent connection is reusable"
                        finally
                            database.Fixture.Dispose()
                    }
            }
        ]
