module ClaimCore.IntegrationTests.HostingTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private connectionString (database: PostgresFixture.Database) =
    let settings =
        NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

    settings.Password <- "synthetic-only"
    settings.ConnectionString

[<Tests>]
let tests =
    testSequenced
    <| testList
        "runtime composition"
        [
            testCaseAsync
                "readiness checks schema availability and closes after shutdown without reading case payloads"
                (run (fun database ->
                    task {
                        use! owner = database.OwnerSource.OpenConnectionAsync token

                        let! _ =
                            ClaimCore.Postgres.DatabaseSession.execute
                                owner
                                "REVOKE SELECT ON claimcore.ledger FROM claimcore_runtime; GRANT SELECT(sequence,entry_hash) ON claimcore.ledger TO claimcore_runtime"
                                [||]
                                token

                        let started = Diagnostics.Stopwatch.StartNew()

                        let! opened =
                            ClaimCore.Hosting.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime =
                            opened |> Result.defaultWith (fun _ -> failtest "Runtime refused")

                        Expect.isLessThan
                            started.Elapsed.TotalSeconds
                            10.
                            "Fresh runtime admission takes seconds without payload reads"

                        printfn
                            "Fresh host runtime admitted in %.0f ms without payload SELECT authority"
                            started.Elapsed.TotalMilliseconds

                        let! ready = runtime.Ready token
                        Expect.isTrue ready "Current reachable schema"

                        let! _ =
                            ClaimCore.Postgres.DatabaseSession.execute
                                owner
                                "UPDATE claimcore.schema_migrations SET script_sha256=decode(repeat('00',32),'hex')"
                                [||]
                                token

                        let! mismatch = runtime.Ready token
                        Expect.isFalse mismatch "Changed checksum refuses readiness"
                        let! _ = runtime.Shutdown(TimeSpan.FromSeconds 1.)
                        let! closed = runtime.Ready token
                        Expect.isFalse closed "Closed admission"
                    }))
            testCaseAsync
                "admitted runtime binds principal and closes admission on shutdown"
                (run (fun database ->
                    task {
                        let! _ = editor database.Fixture

                        let! opened =
                            ClaimCore.Hosting.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime =
                            opened |> Result.defaultWith (fun _ -> failtest "Runtime refused")

                        Expect.equal runtime.BusinessTimeZone "Etc/UTC" "Stored calendar"
                        let actor = runtime.Actor database.Fixture.Owner

                        let! result =
                            actor.Execute(
                                request
                                    30
                                    "synthetic-hosting"
                                    0L
                                    (ClaimCore.Domain.Command.Open registration),
                                token
                            )

                        accepted result |> ignore
                        let! closed = runtime.Shutdown(TimeSpan.FromSeconds 1.)
                        Expect.equal closed ShutdownOutcome.Drained "Shutdown"
                        let! after = actor.Definition token

                        Expect.equal
                            after
                            (ClaimCore.Application.QueryOutcome.Failed
                                ClaimCore.Application.CoreFault.StoreUnavailable)
                            "Admission closed"
                    }))
            for name, change, expected in
                [
                    "absent schema",
                    "DROP SCHEMA claimcore CASCADE",
                    ClaimCore.Hosting.RuntimeOpenFault.SchemaMismatch
                    "behind",
                    "DELETE FROM claimcore.schema_migrations",
                    ClaimCore.Hosting.RuntimeOpenFault.SchemaMismatch
                    "ahead",
                    "INSERT INTO claimcore.schema_migrations VALUES(2,'extra',decode(repeat('00',32),'hex'),clock_timestamp())",
                    ClaimCore.Hosting.RuntimeOpenFault.SchemaMismatch
                    "foreign",
                    "DROP SCHEMA claimcore CASCADE;CREATE SCHEMA claimcore;GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime",
                    ClaimCore.Hosting.RuntimeOpenFault.SchemaMismatch
                    "superuser",
                    "ALTER ROLE claimcore_runtime SUPERUSER",
                    ClaimCore.Hosting.RuntimeOpenFault.UnsafeRole
                    "owner",
                    "ALTER SCHEMA claimcore OWNER TO claimcore_runtime",
                    ClaimCore.Hosting.RuntimeOpenFault.UnsafeRole
                    "missing installation",
                    "DELETE FROM claimcore.installation",
                    ClaimCore.Hosting.RuntimeOpenFault.InstallationInvalid
                    "zone",
                    "UPDATE claimcore.installation SET business_time_zone='Invalid/Zone'",
                    ClaimCore.Hosting.RuntimeOpenFault.InstallationInvalid
                    "head",
                    "UPDATE claimcore.ledger_head SET sequence=1",
                    ClaimCore.Hosting.RuntimeOpenFault.HeadMismatch
                ] do
                testCaseAsync
                    ($"typed runtime open refusal: {name}")
                    (run (fun database ->
                        task {
                            use! connection = database.OwnerSource.OpenConnectionAsync()
                            let! _ = DatabaseSession.execute connection change [||] token

                            let! opened =
                                ClaimCore.Hosting.Runtime.openConnection
                                    (connectionString database)
                                    token

                            match opened with
                            | Error fault -> Expect.equal fault expected "Typed refusal"
                            | Ok runtime ->
                                runtime.Dispose()
                                failtest "Unexpected admission"
                        }))
            testCaseAsync
                "remote unverified transport and pre-cancellation have typed faults"
                (async {
                    let! opened =
                        ClaimCore.Hosting.Runtime.openConnection
                            "Host=remote.example;SSL Mode=Require"
                            token
                        |> Async.AwaitTask

                    Expect.equal
                        (opened |> Result.map (fun runtime -> runtime.Dispose()))
                        (Error ClaimCore.Hosting.RuntimeOpenFault.InvalidTransport)
                        "Transport refused without connecting"

                    use cancelled = new CancellationTokenSource()
                    cancelled.Cancel()

                    let! result =
                        ClaimCore.Hosting.Runtime.openConnection "Host=localhost" cancelled.Token
                        |> Async.AwaitTask

                    Expect.equal
                        (result |> Result.map (fun runtime -> runtime.Dispose()))
                        (Error ClaimCore.Hosting.RuntimeOpenFault.Cancelled)
                        "Cancellation surfaced"
                })
            testCaseAsync
                "expired drain bound preserves a real admitted transaction"
                (run (fun database ->
                    task {
                        let! _ = editor database.Fixture

                        let! opened =
                            ClaimCore.Hosting.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime =
                            opened |> Result.defaultWith (fun _ -> failtest "Runtime refused")

                        use! holder = database.OwnerSource.OpenConnectionAsync()
                        use! transaction = holder.BeginTransactionAsync()
                        let! _ = Transactions.head holder token

                        let running =
                            (runtime.Actor database.Fixture.Owner)
                                .Execute(
                                    request
                                        40
                                        "synthetic-drain"
                                        0L
                                        (ClaimCore.Domain.Command.Open registration),
                                    token
                                )

                        let deadline = DateTimeOffset.UtcNow.AddSeconds 3.
                        let mutable waiting = false
                        use! observer = database.OwnerSource.OpenConnectionAsync()

                        while not waiting && DateTimeOffset.UtcNow < deadline do
                            let! rows =
                                DatabaseSession.rows
                                    observer
                                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock')"
                                    [||]
                                    token

                            waiting <- DatabaseSession.value<bool> 0 rows.Head

                            if not waiting then
                                do! System.Threading.Tasks.Task.Delay 10

                        Expect.isTrue waiting "Admitted writer is waiting for the head"
                        let! shutdown = runtime.Shutdown(TimeSpan.Zero)
                        Expect.equal shutdown ShutdownOutcome.BoundExpired "Bound expires"
                        do! transaction.CommitAsync()
                        let! outcome = running
                        accepted outcome |> ignore
                        let! verified = Verification.verify database.OwnerSource None token

                        Expect.equal
                            verified.Sequence
                            3L
                            "Admitted transaction still committed exactly once"
                    }))
            testCase "unsupported server metadata maps to the typed host refusal" (fun () ->
                Admission.serverMajor ServerSupport.major

                for major in [ ServerSupport.major - 1; ServerSupport.major + 1 ] do
                    let mutable refused = false

                    try
                        Admission.serverMajor major
                    with AdmissionRefusal fault ->
                        refused <- true

                        Expect.equal
                            (ClaimCore.Hosting.Runtime.fault fault)
                            ClaimCore.Hosting.RuntimeOpenFault.UnsupportedServer
                            "Same admission guard and host mapping"

                    Expect.isTrue refused "Unsupported major rejected")
        ]
