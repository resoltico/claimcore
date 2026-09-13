module ClaimCore.IntegrationTests.CoreBoundaryTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private executeOwner sql =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private expectAclRejected grantSql revokeSql =
    executeOwner grantSql

    try
        use database = new PostgresStore(appConnection ())

        Expect.equal
            (database.CheckSchema() |> await)
            (Error CoreFailure.SchemaMismatch)
            "Runtime rejects the expanded ACL"
    finally
        executeOwner revokeSql

    use restored = new PostgresStore(appConnection ())
    Expect.equal (restored.CheckSchema() |> await) (Ok()) "The exact runtime ACL is restored"

let private quotedDatabase () =
    let value =
        NpgsqlConnectionStringBuilder(adminConnection ()).Database
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Test database name is required")

    use builder = new NpgsqlCommandBuilder()
    builder.QuoteIdentifier(value)

let private runtimeTests =
    testList
        "composed runtime"
        [
            testCase "host exposes the same core API used by a native renderer" (fun () ->
                let runtime =
                    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
                    |> await
                    |> accepted

                use lifetime = runtime

                let request =
                    {
                        OperationId = Guid.NewGuid()
                        CaseReference = "RUNTIME-" + Guid.NewGuid().ToString("N")
                        ExpectedVersion = 0L
                        Kind = CommandKind.Open
                        Values =
                            [
                                "incidentDate", registration.IncidentDate
                                "incidentNotificationDate", registration.IncidentNotificationDate
                                "incidentCountry", registration.IncidentCountry
                                "claimantName", registration.ClaimantName
                                "insurerName", registration.InsurerName
                                "claimedAmount", registration.ClaimedAmount
                                "claimedCurrency", registration.ClaimedCurrency
                            ]
                    }

                let response = lifetime.Core.Execute(request, CancellationToken.None) |> await

                match response with
                | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted receipt, _) ->
                    Expect.isTrue
                        (receipt.Snapshot.Fields.ClaimantName = registration.ClaimantName)
                        "Through real composition"

                    Expect.equal
                        receipt.Snapshot.Fields.Status
                        CaseStatus.Opened
                        "Historical snapshot"
                | _ -> failtest "Expected an accepted core receipt."

                match
                    lifetime.Core.Get(request.CaseReference, CancellationToken.None) |> await
                with
                | QueryOutcome.Succeeded(Lookup.Found view) ->
                    Expect.contains
                        view.AvailableCommands
                        CommandKind.Decide
                        "Core supplied current actions"
                | _ -> failtest "Current core query failed")
        ]

type private DisposeCounter() =
    let mutable value = 0
    member _.Increment() = Interlocked.Increment(&value) |> ignore
    member _.Value = Volatile.Read(&value)

let private countedSource (disposeCount: DisposeCounter) =
    { new IDisposable with
        member _.Dispose() = disposeCount.Increment()
    }

let private disposalClosesAdmission () =
    let disposeCount = DisposeCounter()

    let admission =
        new RuntimeAdmission(countedSource disposeCount, TimeSpan.FromSeconds 2.)

    use held = admission.Admit()
    let disposing = Task.Run(fun () -> (admission :> IDisposable).Dispose())

    Expect.isTrue
        (SpinWait.SpinUntil(
            (fun () ->
                try
                    use _unexpected = admission.Admit()
                    false
                with :? ObjectDisposedException ->
                    true),
            2000
        ))
        "Disposal must close admission"

    Expect.equal disposeCount.Value 0 "Admitted work still owns the source"
    Expect.isFalse disposing.IsCompleted "Disposal waits for the held operation"
    held.Dispose()
    Expect.isTrue (disposing.Wait(2000)) "Disposal finishes after the lease"
    Expect.equal disposeCount.Value 1 "One owner disposes the source exactly once"

let private boundedDisposalDefersCleanup () =
    let disposeCount = DisposeCounter()

    let admission =
        new RuntimeAdmission(countedSource disposeCount, TimeSpan.FromMilliseconds 50.)

    use held = admission.Admit()
    let timer = Stopwatch.StartNew()
    (admission :> IDisposable).Dispose()
    timer.Stop()
    Expect.isTrue (timer.Elapsed < TimeSpan.FromSeconds 2.) "Drain is bounded"
    Expect.equal disposeCount.Value 0 "Timed-out disposal cannot close an admitted source"
    held.Dispose()

    Expect.isTrue
        (SpinWait.SpinUntil((fun () -> disposeCount.Value = 1), 2000))
        "Final lease performs deferred cleanup"

let private disposedRuntimeRefusesRetainedFacades () =
    let runtime =
        Runtime.OpenPostgres(appConnection (), CancellationToken.None)
        |> await
        |> accepted

    let core = runtime.Core
    let recovery = core.Recovery
    (runtime :> IDisposable).Dispose()

    Expect.throwsT<ObjectDisposedException>
        (fun () -> core.Describe() |> ignore)
        "Describe refuses a disposed runtime"

    Expect.throwsT<ObjectDisposedException>
        (fun () -> core.Get("NO-SUCH-SYNTHETIC-CASE", CancellationToken.None) |> await |> ignore)
        "Queries refuse a disposed runtime"

    Expect.throwsT<ObjectDisposedException>
        (fun () -> recovery.List(None, 1, CancellationToken.None) |> await |> ignore)
        "Retained recovery facade also refuses admission"

let private cancelledOpeningReturnsTypedFault () =
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()

    match Runtime.OpenPostgres(appConnection (), cancellation.Token) |> await with
    | Error RuntimeOpenFault.RuntimeCancelled -> ()
    | _ -> failtest "Pre-cancelled opening must return RuntimeCancelled"

let private runtimeAdmissionTests =
    testList
        "runtime lifecycle"
        [
            testCase
                "[CC-RUN-001] disposal closes admission and drains a held operation"
                disposalClosesAdmission
            testCase
                "[CC-RUN-001] bounded disposal defers source cleanup until the final lease"
                boundedDisposalDefersCleanup
            testCase
                "[CC-RUN-001] disposed runtime refuses retained normal and recovery facades"
                disposedRuntimeRefusesRetainedFacades
            testCase
                "[CC-RUN-001] cancelled opening returns a safe typed fault"
                cancelledOpeningReturnsTypedFault
        ]

let private admissionTests =
    testList
        "connection admission"
        [
            testCase
                "direct store call cannot bypass runtime role verification by omitting CheckSchema"
                (fun () ->
                    Expect.throwsT<ArgumentException>
                        (fun () -> new PostgresStore(adminConnection ()) |> ignore)
                        "The adapter rejects a configured owner identity before creating its pool")
            testCase "an elevated session cannot impersonate the runtime role" (fun () ->
                use connection = new Npgsql.NpgsqlConnection(adminConnection ())
                connection.Open()
                use changeRole = new Npgsql.NpgsqlCommand("SET ROLE claimcore_app", connection)
                changeRole.ExecuteNonQuery() |> ignore
                let mutable refused = false

                try
                    RuntimeDatabase.requireCompatible connection
                with RuntimeDatabaseMismatch ->
                    refused <- true

                Expect.isTrue
                    refused
                    "Both authenticated session_user and effective current_user are required")
            testCase
                "dangerous runtime connection switches are refused before database access"
                (fun () ->
                    let expectRejected (change: Npgsql.NpgsqlConnectionStringBuilder -> unit) =
                        let builder = Npgsql.NpgsqlConnectionStringBuilder(appConnection ())
                        change builder

                        Expect.throwsT<ArgumentException>
                            (fun () -> new PostgresStore(builder.ConnectionString) |> ignore)
                            "Connection policy"

                    expectRejected (fun builder -> builder.Options <- "-c role=claimcore_app")
                    expectRejected (fun builder -> builder.NoResetOnClose <- true)
                    expectRejected (fun builder -> builder.LogParameters <- true)
                    expectRejected (fun builder -> builder.PersistSecurityInfo <- true))
            testCase
                "host rejects elevated connection rather than handing a renderer a store"
                (fun () ->
                    match
                        Runtime.OpenPostgres(adminConnection (), CancellationToken.None) |> await
                    with
                    | Error RuntimeOpenFault.RuntimeConfigurationInvalid -> ()
                    | Error _ -> failtest "Owner runtime must fail connection admission."
                    | Ok runtime ->
                        use lifetime = runtime
                        lifetime.Core.Describe() |> ignore
                        failtest "Unexpected elevated runtime")
        ]

let private aclTests =
    testList
        "exact runtime ACL"
        [
            testCase "MAINTAIN is outside the cases-table envelope" (fun () ->
                expectAclRejected
                    "GRANT MAINTAIN ON claimcore.cases TO claimcore_app"
                    "REVOKE MAINTAIN ON claimcore.cases FROM claimcore_app")
            testCase "database TEMPORARY is outside the runtime envelope" (fun () ->
                let database = quotedDatabase ()

                expectAclRejected
                    $"GRANT TEMPORARY ON DATABASE {database} TO claimcore_app"
                    $"REVOKE TEMPORARY ON DATABASE {database} FROM claimcore_app")
            testCase "column UPDATE is rejected even without table UPDATE" (fun () ->
                expectAclRejected
                    "GRANT UPDATE (recorded_by) ON claimcore.case_changes TO claimcore_app"
                    "REVOKE UPDATE (recorded_by) ON claimcore.case_changes FROM claimcore_app")
            testCase "PUBLIC column grants are rejected" (fun () ->
                expectAclRejected
                    "GRANT UPDATE (recorded_by) ON claimcore.case_changes TO PUBLIC"
                    "REVOKE UPDATE (recorded_by) ON claimcore.case_changes FROM PUBLIC")
            testCase "grant options are rejected on otherwise required privileges" (fun () ->
                expectAclRejected
                    "GRANT SELECT ON claimcore.case_changes TO claimcore_app WITH GRANT OPTION"
                    "REVOKE GRANT OPTION FOR SELECT ON claimcore.case_changes FROM claimcore_app")
        ]

let tests =
    testList
        "production core boundary"
        [ runtimeTests; runtimeAdmissionTests; admissionTests; aclTests ]
