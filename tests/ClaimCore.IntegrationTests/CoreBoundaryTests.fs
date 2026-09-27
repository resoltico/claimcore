module ClaimCore.IntegrationTests.CoreBoundaryTests

open System
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CoreRuntimeLifecycleTests

let private executeOwner sql =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private expectAclRejected grantSql revokeSql =
    executeOwner grantSql

    try
        use source = RuntimeDataSource.create (appConnection ())

        Expect.throwsT<RuntimeDatabaseMismatch>
            (fun () -> use _connection = RuntimeDatabase.openConnection source in ())
            "Runtime rejects the expanded ACL"
    finally
        executeOwner revokeSql

    use restored = RuntimeDataSource.create (appConnection ())
    use _connection = RuntimeDatabase.openConnection restored
    ()

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
                    witnessedOpen (appConnection ()) CancellationToken.None |> await |> accepted

                use lifetime = runtime
                let core = lifetime.ForActor(ActorBoundStoreFixture.actorPrincipal ())

                let request =
                    openRequest (Guid.NewGuid()) ("RUNTIME-" + Guid.NewGuid().ToString("N"))

                let response = core.Execute(request, CancellationToken.None) |> await

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

                match core.Get(request.CaseReference, CancellationToken.None) |> await with
                | QueryOutcome.Succeeded(Lookup.Found view) ->
                    Expect.contains
                        view.AvailableCommands
                        CommandKind.Decide
                        "Core supplied current actions"
                | _ -> failtest "Current core query failed")
        ]


let private dangerousConnectionSwitches () =
    let expectRejected (change: NpgsqlConnectionStringBuilder -> unit) =
        let builder = NpgsqlConnectionStringBuilder(appConnection ())
        change builder

        Expect.throwsT<ArgumentException>
            (fun () -> use _source = RuntimeDataSource.create builder.ConnectionString in ())
            "Connection policy"

    expectRejected (fun builder -> builder.Options <- "-c role=claimcore_app")
    expectRejected (fun builder -> builder.NoResetOnClose <- true)
    expectRejected (fun builder -> builder.LogParameters <- true)
    expectRejected (fun builder -> builder.PersistSecurityInfo <- true)

    for mode in [ SslMode.Disable; SslMode.Prefer; SslMode.Require; SslMode.VerifyCA ] do
        expectRejected (fun builder ->
            builder.Host <- "database.example.invalid"
            builder.SslMode <- mode)

    let verified = NpgsqlConnectionStringBuilder(appConnection ())
    verified.Host <- "database.example.invalid"
    verified.SslMode <- SslMode.VerifyFull
    use accepted = RuntimeDataSource.create verified.ConnectionString

    Expect.isNotNull
        (box accepted)
        "A hostname-verified remote transport passes configuration admission"

let private admissionTests =
    testList
        "connection admission"
        [
            testCase "runtime data source rejects an owner login before database access" (fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () -> use _source = RuntimeDataSource.create (adminConnection ()) in ())
                    "Runtime rejects a configured owner identity before creating its pool")
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
                dangerousConnectionSwitches
            testCase
                "host rejects elevated connection rather than handing a renderer a store"
                (fun () ->
                    match witnessedOpen (adminConnection ()) CancellationToken.None |> await with
                    | Error RuntimeOpenFault.RuntimeConfigurationInvalid -> ()
                    | Error _ -> failtest "Owner runtime must fail connection admission."
                    | Ok runtime ->
                        use lifetime = runtime
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
                    "GRANT UPDATE (accepted_actor_id) ON claimcore.case_changes TO claimcore_app"
                    "REVOKE UPDATE (accepted_actor_id) ON claimcore.case_changes FROM claimcore_app")
            testCase "PUBLIC column grants are rejected" (fun () ->
                expectAclRejected
                    "GRANT UPDATE (accepted_actor_id) ON claimcore.case_changes TO PUBLIC"
                    "REVOKE UPDATE (accepted_actor_id) ON claimcore.case_changes FROM PUBLIC")
            testCase "grant options are rejected on otherwise required privileges" (fun () ->
                expectAclRejected
                    "GRANT SELECT ON claimcore.case_changes TO claimcore_app WITH GRANT OPTION"
                    "REVOKE GRANT OPTION FOR SELECT ON claimcore.case_changes FROM claimcore_app")
        ]

let tests =
    testList
        "production core boundary"
        [ runtimeTests; runtimeAdmissionTests; admissionTests; aclTests ]
