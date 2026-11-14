module ClaimCore.IntegrationTests.OperatorFaultProcessTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application.Contracts
open ClaimCore.Server.Administration
open ClaimCore.Postgres
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.TestSupport

let private publishedFaults () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask
        let directory = PrivateTestPaths.newPrivateDirectory "claimcore-operator-fault-"
        let mutable completed = false

        try
            let configuration =
                typeof<DatabaseSuccess>
                    .Assembly.GetCustomAttributes(
                        typeof<System.Reflection.AssemblyConfigurationAttribute>,
                        false
                    )
                |> Array.exactlyOne
                |> unbox<System.Reflection.AssemblyConfigurationAttribute>
                |> _.Configuration

            let! binary =
                AdministrationProcess.publishConfiguration
                    configuration
                    (Path.Combine(directory, "published"))
                |> Async.AwaitTask

            let privateText name value =
                AdministrationProcess.privateText directory name value

            let connection = privateText "connection" (database.Container.GetConnectionString())

            let fault connectionFile arguments expected exit =
                async {
                    let! reply =
                        AdministrationProcess.invoke
                            binary
                            connectionFile
                            arguments
                            (ExitCode.value exit)
                        |> Async.AwaitTask

                    Expect.equal
                        (reply.GetProperty("kind").GetString())
                        "OPERATOR_FAULT"
                        "Dedicated owner error envelope"

                    Expect.equal
                        (reply.GetProperty("fault").GetString())
                        expected
                        "Specific safe operator code"

                    Expect.equal
                        (reply.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                        (set [ "kind"; "fault" ])
                        "No raw arguments, exceptions or provider details"

                    let body = reply.GetRawText()

                    for secret in
                        [ directory; "private-secret-sentinel"; "Password="; "synthetic-only" ] do
                        Expect.isFalse (body.Contains secret) "Private values are excluded"
                }

            let invokeWithoutConnection arguments =
                async {
                    let config = AdministrationProcess.start (binary :: arguments)

                    let! exit, body, errors =
                        AdministrationProcess.execute config |> Async.AwaitTask

                    Expect.equal
                        exit
                        (ExitCode.value ExitCode.Rejected)
                        "Missing configuration is refused"

                    Expect.equal errors "" "No private stderr detail"
                    use reply = JsonDocument.Parse body

                    Expect.equal
                        (reply.RootElement.GetProperty("fault").GetString())
                        "CONNECTION_FILE_REQUIRED"
                        "Missing file is distinct from bad database"
                }

            let emptyInvocation = AdministrationProcess.start [ binary ]

            let! emptyExit, emptyBody, emptyErrors =
                AdministrationProcess.execute emptyInvocation |> Async.AwaitTask

            Expect.equal emptyExit 64 "A server invocation requires a subcommand"
            Expect.equal emptyErrors "" "Usage has no exception detail"
            use emptyReply = JsonDocument.Parse emptyBody

            Expect.equal
                (emptyReply.RootElement.GetProperty("kind").GetString())
                "USAGE_ERROR"
                "No command prints usage"

            Expect.stringContains emptyBody "serve" "Usage includes serving"

            do! invokeWithoutConnection [ "status" ]

            do!
                invokeWithoutConnection
                    [
                        "initialize"
                        "Etc/UTC"
                        "--owner-issuer"
                        "https://issuer.example/"
                        "--owner-subject"
                        "synthetic-owner"
                        "--owner-name"
                        "Synthetic owner"
                    ]

            do!
                fault
                    (Path.Combine(directory, "private-secret-sentinel"))
                    [ "status" ]
                    "CONNECTION_FILE_REFUSED"
                    ExitCode.Rejected

            let empty = privateText "empty" "  "
            do! fault empty [ "status" ] "CONNECTION_FILE_INVALID" ExitCode.Rejected

            let malformed =
                privateText
                    "malformed"
                    "Host=private-secret-sentinel;UnknownPrivateKey=private-secret-sentinel"

            do! fault malformed [ "status" ] "CONNECTION_INVALID" ExitCode.Rejected

            let remote =
                privateText
                    "unsafe-transport"
                    "Host=private-secret-sentinel.example;SSL Mode=Require"

            do! fault remote [ "status" ] "UNSAFE_TRANSPORT" ExitCode.Rejected
            let utf8 = privateText "invalid-utf8" "placeholder"
            File.WriteAllBytes(utf8, [| 0xffuy; 0xfeuy |])
            do! fault utf8 [ "status" ] "CONNECTION_FILE_INVALID" ExitCode.Rejected

            let owner =
                [
                    "--owner-issuer"
                    "https://issuer.example/"
                    "--owner-subject"
                    "owner"
                    "--owner-name"
                    "Owner"
                ]

            do!
                fault
                    connection
                    ([ "initialize"; "private-secret-sentinel" ] @ owner)
                    "TIME_ZONE_INVALID"
                    ExitCode.Rejected

            do!
                fault
                    connection
                    ([ "initialize"; "Etc/UTC" ] @ owner)
                    "ALREADY_INITIALIZED"
                    ExitCode.Rejected

            do!
                fault
                    connection
                    [
                        "initialize"
                        "Etc/UTC"
                        "--owner-issuer"
                        "private-secret-sentinel"
                        "--owner-subject"
                        "owner"
                        "--owner-name"
                        "Owner"
                    ]
                    "INITIAL_OWNER_INVALID"
                    ExitCode.Rejected

            do!
                fault
                    connection
                    [ "private-secret-sentinel" ]
                    "INVOCATION_INVALID"
                    ExitCode.Rejected

            do!
                fault
                    connection
                    [ "initialize"; "Etc/UTC"; "--private-secret-sentinel" ]
                    "INVOCATION_INVALID"
                    ExitCode.Rejected

            let sql statement parameters =
                async {
                    use connection = database.OwnerSource.OpenConnection()

                    let! _ =
                        DatabaseSession.execute
                            connection
                            statement
                            parameters
                            CancellationToken.None
                        |> Async.AwaitTask

                    return ()
                }

            do!
                sql
                    "UPDATE claimcore.baseline_identity SET script_sha256=decode(repeat('00',32),'hex')"
                    [||]

            do! fault connection [ "status" ] "SCHEMA_MISMATCH" ExitCode.Rejected

            do!
                sql
                    "UPDATE claimcore.baseline_identity SET script_sha256=$1"
                    [| box Baseline.digest |]

            // Deliberately bypass the schema CHECK in this owned fixture to exercise stored-format admission.
            do!
                sql
                    "ALTER TABLE claimcore.installation DROP CONSTRAINT installation_installation_format_check; UPDATE claimcore.installation SET installation_format=1"
                    [||]

            do! fault connection [ "status" ] "FORMAT_MISMATCH" ExitCode.Rejected

            do!
                sql
                    "UPDATE claimcore.installation SET installation_format=3; ALTER TABLE claimcore.installation ADD CONSTRAINT installation_installation_format_check CHECK(installation_format=3)"
                    [||]

            do! sql "DELETE FROM claimcore.installation" [||]
            do! fault connection [ "status" ] "INSTALLATION_INVALID" ExitCode.Rejected

            do!
                sql
                    "INSERT INTO claimcore.installation(installation_format,installation_id,business_time_zone) VALUES(3,$1,'Etc/UTC')"
                    [| box database.Fixture.InstallationId |]

            do! sql "DELETE FROM claimcore.ledger_head" [||]
            do! fault connection [ "head" ] "INTEGRITY_FAILURE" ExitCode.Failed

            do!
                sql
                    "INSERT INTO claimcore.ledger_head(sequence,entry_hash) VALUES(0,decode(repeat('00',32),'hex'))"
                    [||]

            do! sql "CREATE ROLE operator_no_access LOGIN PASSWORD 'synthetic-only'" [||]

            let settings =
                NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

            settings.Username <- "operator_no_access"
            settings.Password <- "synthetic-only"
            let denied = privateText "denied" settings.ConnectionString
            do! fault denied [ "status" ] "PERMISSION_DENIED" ExitCode.Rejected
            settings.Password <- "private-secret-sentinel"
            let authentication = privateText "authentication" settings.ConnectionString
            do! fault authentication [ "status" ] "CONNECTION_REJECTED" ExitCode.Rejected

            use waiting = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
            waiting.Start()
            let port = (waiting.LocalEndpoint :?> System.Net.IPEndPoint).Port

            let unavailable =
                privateText "unavailable" ($"Host=127.0.0.1;Port={port};Timeout=1;SSL Mode=Disable")

            do! fault unavailable [ "status" ] "UNAVAILABLE" ExitCode.Failed
            waiting.Stop()

            let! result =
                AdministrationProcess.invoke binary connection [ "status" ] 0 |> Async.AwaitTask

            Expect.equal
                (result.GetProperty("kind").GetString())
                "STATUS"
                "Valid source still works after negative controls"

            completed <- true
        finally
            database.Fixture.Dispose()

            if completed then
                Directory.Delete(directory, true)
            else
                printfn "Private operator failure evidence retained: %s" directory
    }

[<Tests>]
let tests =
    testCaseAsync
        "published owner faults distinguish setup and provider failures without private values"
        (publishedFaults ())
