module ClaimCore.IntegrationTests.OperatorFaultTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Expecto
open Npgsql
open ClaimCore.Application.Contracts
open ClaimCore.Postgres

[<Tests>]
let tests =
    testList
        "owner fault boundaries"
        [
            testCase
                "owner failures have independent bounded meanings and unknown commit takes precedence"
                (fun () ->
                    let cases: (exn * OperatorFault) list =
                        [
                            PostgresException("private provider secret", "ERROR", "ERROR", "42501"),
                            OperatorFault.PermissionDenied
                            PostgresException("private provider secret", "ERROR", "ERROR", "28P01"),
                            OperatorFault.ConnectionRejected
                            PostgresException("private provider secret", "ERROR", "ERROR", "42P01"),
                            OperatorFault.SchemaMismatch
                            PostgresException("private provider secret", "ERROR", "ERROR", "42601"),
                            OperatorFault.InternalFailure
                            NpgsqlException("private connection secret"), OperatorFault.Unavailable
                            TimeoutException("private timeout detail"), OperatorFault.Unavailable
                            IOException("private path"), OperatorFault.Unavailable
                            ArgumentException("private programming detail"),
                            OperatorFault.InternalFailure
                            RecordCorruptException("private stored bytes"),
                            OperatorFault.IntegrityFailure
                            OperationCanceledException(), OperatorFault.Cancelled
                            OperatorFaultException OperatorFault.AlreadyInitialized,
                            OperatorFault.AlreadyInitialized
                            OperatorFaultException OperatorFault.FormatMismatch,
                            OperatorFault.FormatMismatch
                            MaintenanceRequiredException(), OperatorFault.MaintenanceRequired
                        ]

                    for error, expected in cases do
                        Expect.equal
                            (AdministrationFailures.classify error)
                            (AdministrationFailure.Fault expected)
                            "Typed source and provider codes determine the safe fault"

                    Expect.equal
                        (AdministrationFailures.classify (AdministrationOutcomeUnknownException()))
                        AdministrationFailure.OutcomeUnknown
                        "Unknown commit is never reduced to retryable provider failure")
            testCase
                "source validation emits typed setup faults before database mutation"
                (fun () ->
                    for action, expected in
                        [
                            (fun () -> Baseline.zone "private-invalid-zone" |> ignore),
                            OperatorFault.TimeZoneInvalid
                            (fun () -> ServerSupport.require (System.Version(17, 0))),
                            OperatorFault.UnsupportedServer
                        ] do
                        let mutable actual = None

                        try
                            action ()
                        with :? OperatorFaultException as error ->
                            actual <- Some error.Fault

                        Expect.equal
                            actual
                            (Some expected)
                            "No string parsing or private value in setup refusal")
            testCase
                "owner synchronous cleanup captures failure without replacing a confirmed result or primary exception"
                (fun () ->
                    use activity = new Activity("ClaimCore.Owner.CleanupWitness")
                    activity.Start() |> ignore
                    let observed = ConcurrentQueue<FailureDiagnostic>()

                    use subscription =
                        FailureDiagnostics.listener.Subscribe(
                            { new IObserver<KeyValuePair<string, obj>> with
                                member _.OnNext value =
                                    match Activity.Current with
                                    | null -> ()
                                    | current when current.TraceId = activity.TraceId ->
                                        observed.Enqueue(unbox value.Value)
                                    | _ -> ()

                                    failwith "observer detail must not escape"

                                member _.OnError _ = ()
                                member _.OnCompleted() = ()
                            }
                        )

                    let confirmed = 42
                    FailureDiagnostics.cleanupSync (fun () -> failwith "provider secret")
                    Expect.equal confirmed 42 "Cleanup does not replace confirmed output"
                    let primary = InvalidOperationException("primary private witness")
                    let mutable actual = None

                    try
                        try
                            raise primary
                        finally
                            FailureDiagnostics.cleanupSync (fun () -> failwith "secondary detail")
                    with error ->
                        actual <- Some error

                    Expect.isTrue
                        (actual |> Option.exists (fun error -> obj.ReferenceEquals(error, primary)))
                        "Primary exception identity survives cleanup and observer failures"

                    Expect.equal observed.Count 2 "Each cleanup failure is captured independently"

                    for diagnostic in observed do
                        Expect.equal
                            diagnostic.Phase
                            FailurePhase.Cleanup
                            "Cleanup is distinguished from operation failure"

                        Expect.equal
                            diagnostic.Classification
                            FailureClass.Internal
                            "Only a bounded classification is retained")
        ]
