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

                        let observed = ResizeArray<FailureDiagnostic>()

                        use _subscription =
                            FailureDiagnostics.listener.Subscribe(
                                { new IObserver<Collections.Generic.KeyValuePair<string, obj>> with
                                    member _.OnNext value =
                                        observed.Add(unbox value.Value)

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

                                        do! transaction.CommitAsync()
                                        return 42
                                    })

                        do!
                            FailureDiagnostics.cleanup (fun () ->
                                ValueTask(
                                    Task.FromException(
                                        InvalidOperationException
                                            "synthetic credential SQL payload secret"
                                    )
                                ))

                        Expect.equal
                            result
                            42
                            "Confirmed result survives observer and cleanup failures"

                        Expect.equal observed.Count 1 "Cleanup remains diagnosable"
                        Expect.equal observed[0].Phase FailurePhase.Cleanup "Safe phase"

                        Expect.equal
                            observed[0].Classification
                            FailureClass.Internal
                            "Safe classification"

                        Expect.isLessThanOrEqual observed[0].Methods.Length 8 "Bounded methods"
                        Expect.notEqual observed[0].Correlation Guid.Empty "Internal correlation"
                        let serialized = System.Text.Json.JsonSerializer.Serialize observed[0]

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
                        let observed = ResizeArray<FailureDiagnostic>()

                        use _subscription =
                            FailureDiagnostics.listener.Subscribe(
                                { new IObserver<Collections.Generic.KeyValuePair<string, obj>> with
                                    member _.OnNext value = observed.Add(unbox value.Value)
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
