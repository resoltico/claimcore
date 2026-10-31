module ClaimCore.IntegrationTests.FailureDiagnosticsTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Postgres
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

                        use _diagnostic = FailureDiagnostics.scope FailurePhase.Open
                        FailureDiagnostics.report (IO.IOException "synthetic SQL and principal")
                        FailureDiagnostics.phase FailurePhase.Append
                        FailureDiagnostics.report (TimeoutException "synthetic token")
                        FailureDiagnostics.phase FailurePhase.Commit
                        FailureDiagnostics.report (OperationCanceledException "synthetic payload")

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
                            1
                            "One operation correlation"
                    }
            }
        ]
