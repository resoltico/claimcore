module ClaimCore.IntegrationTests.HostingTests

open ClaimCore.Application
open ClaimCore.TestSupport

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Web.Composition
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

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

let private configuredRuntime
    (result: Result<ClaimCore.Web.Composition.IRuntime, ClaimCore.Application.RuntimeOpenFault>)
    =
    let runtime = result |> Result.defaultWith (fun _ -> failtest "Runtime refused")
    runtime

let private call (runtime: ClaimCore.Web.Composition.IRuntime) principal action =
    task {
        let! result = runtime.WithActor(principal, action)
        return result |> Option.defaultWith (fun () -> failtest "Expected admitted operation")
    }

let private runtimeCursors (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        for n, reference in [ 10, "CURSOR-A"; 11, "CURSOR-B" ] do
            let! result =
                (Core.execute
                    owner
                    (OperationInput.Case(
                        request
                            n
                            reference
                            0L
                            (ClaimCore.Domain.UnvalidatedCaseAct.Open registration)
                    ))
                    (token))

            accepted result |> ignore

        let! first =
            ClaimCore.Web.Composition.Runtime.openConnection (connectionString database) token

        use first = configuredRuntime first

        let! page =
            call first database.Fixture.Owner (fun actor ->
                (Core.list actor ({ AfterCursor = None; Limit = 1 }) (token)))

        let cursor =
            match page with
            | ReadReply.Succeeded page -> page.NextCursor
            | _ -> failtest "Missing page"

        Expect.isSome cursor "A page is available"

        let! same =
            call first database.Fixture.Owner (fun actor ->
                (Core.list actor ({ AfterCursor = cursor; Limit = 2 }) (token)))

        match same with
        | ReadReply.Succeeded page ->
            Expect.equal page.Items.Length 1 "Every facade accepts the plain position"
        | _ -> failtest "Same-runtime continuation refused"

        let! second =
            ClaimCore.Web.Composition.Runtime.openConnection (connectionString database) token

        use second = configuredRuntime second

        let! continued =
            call second database.Fixture.Owner (fun actor ->
                (Core.list actor ({ AfterCursor = cursor; Limit = 2 }) (token)))

        match continued with
        | ReadReply.Succeeded page ->
            Expect.equal page.Items.Length 1 "Plain positions survive runtime restart"
        | _ -> failtest "Restart continuation refused"

    }

let private readinessCancellation database =
    task {
        let! opened =
            ClaimCore.Web.Composition.Runtime.openConnection (connectionString database) token

        use runtime = configuredRuntime opened
        use! owner = database.OwnerSource.OpenConnectionAsync token
        use! transaction = owner.BeginTransactionAsync token

        use blocking =
            new NpgsqlCommand(
                "LOCK claimcore.baseline_identity IN ACCESS EXCLUSIVE MODE",
                owner,
                transaction
            )

        let! _ = blocking.ExecuteNonQueryAsync token
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 2.)
        let watch = Diagnostics.Stopwatch.StartNew()
        let! blocked = runtime.Ready deadline.Token
        Expect.isFalse blocked "Cancelled schema query cannot report ready"

        Expect.isTrue
            deadline.IsCancellationRequested
            "Real blocked query reaches cancellation deadline"

        Expect.isLessThan watch.Elapsed.TotalSeconds 8. "Actual provider cancellation/cleanup bound"

        printfn
            "Readiness blocked query and provider cleanup: %.0f ms"
            watch.Elapsed.TotalMilliseconds

        do! transaction.RollbackAsync token
        let! recovered = runtime.Ready token
        Expect.isTrue recovered "Same runtime pool recovers after provider cleanup"
    }

[<Tests>]
let tests =
    testList
        "runtime composition"
        [
            testCaseAsync
                "readiness cancellation reaches isolated PostgreSQL and recovers its pool"
                (run readinessCancellation)
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
                            ClaimCore.Web.Composition.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime = configuredRuntime opened

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
                                "UPDATE claimcore.baseline_identity SET script_sha256=decode(repeat('00',32),'hex')"
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
                        let! _ = caseWorker database.Fixture

                        let! opened =
                            ClaimCore.Web.Composition.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime = configuredRuntime opened

                        Expect.equal runtime.BusinessTimeZone "Etc/UTC" "Stored calendar"

                        let! result =
                            call runtime database.Fixture.Owner (fun actor ->
                                (Core.execute
                                    actor
                                    (OperationInput.Case(
                                        request
                                            30
                                            "synthetic-hosting"
                                            0L
                                            (ClaimCore.Domain.UnvalidatedCaseAct.Open registration)
                                    ))
                                    (token)))

                        accepted result |> ignore
                        let! closed = runtime.Shutdown(TimeSpan.FromSeconds 1.)
                        Expect.equal closed ShutdownOutcome.Drained "Shutdown"

                        let! after =
                            runtime.WithActor(
                                database.Fixture.Owner,
                                fun actor -> (Core.context actor () token)
                            )

                        Expect.isNone after "Admission closed"
                    }))
            for name, change, expected in
                [
                    "absent schema",
                    "DROP SCHEMA claimcore CASCADE",
                    ClaimCore.Application.RuntimeOpenFault.SchemaMismatch
                    "missing baseline identity",
                    "DELETE FROM claimcore.baseline_identity",
                    ClaimCore.Application.RuntimeOpenFault.SchemaMismatch
                    "unknown baseline identity",
                    "UPDATE claimcore.baseline_identity SET identifier='unknown'",
                    ClaimCore.Application.RuntimeOpenFault.SchemaMismatch
                    "foreign",
                    "DROP SCHEMA claimcore CASCADE;CREATE SCHEMA claimcore;GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime",
                    ClaimCore.Application.RuntimeOpenFault.SchemaMismatch
                    "superuser",
                    "ALTER ROLE claimcore_runtime SUPERUSER",
                    ClaimCore.Application.RuntimeOpenFault.UnsafeRole
                    "owner",
                    "ALTER SCHEMA claimcore OWNER TO claimcore_runtime",
                    ClaimCore.Application.RuntimeOpenFault.UnsafeRole
                    "missing installation",
                    "DELETE FROM claimcore.installation",
                    ClaimCore.Application.RuntimeOpenFault.InstallationInvalid
                    "zone",
                    "UPDATE claimcore.installation SET business_time_zone='Invalid/Zone'",
                    ClaimCore.Application.RuntimeOpenFault.InstallationInvalid
                    "head",
                    "UPDATE claimcore.ledger_head SET sequence=1",
                    ClaimCore.Application.RuntimeOpenFault.HeadMismatch
                ] do
                testCaseAsync
                    ($"typed runtime open refusal: {name}")
                    (run (fun database ->
                        task {
                            use! connection = database.OwnerSource.OpenConnectionAsync()
                            let! _ = DatabaseSession.execute connection change [||] token

                            let! opened =
                                ClaimCore.Web.Composition.Runtime.openConnection
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
                        ClaimCore.Web.Composition.Runtime.openConnection
                            "Host=remote.example;SSL Mode=Require"
                            token
                        |> Async.AwaitTask

                    Expect.equal
                        (opened |> Result.map (fun runtime -> runtime.Dispose()))
                        (Error ClaimCore.Application.RuntimeOpenFault.InvalidTransport)
                        "Transport refused without connecting"

                    use cancelled = new CancellationTokenSource()
                    cancelled.Cancel()

                    let! result =
                        ClaimCore.Web.Composition.Runtime.openConnection
                            "Host=localhost"
                            cancelled.Token
                        |> Async.AwaitTask

                    Expect.equal
                        (result |> Result.map (fun runtime -> runtime.Dispose()))
                        (Error ClaimCore.Application.RuntimeOpenFault.Cancelled)
                        "Cancellation surfaced"
                })
            testCaseAsync
                "expired drain bound preserves a real admitted transaction"
                (run (fun database ->
                    task {
                        let! _ = caseWorker database.Fixture

                        let! opened =
                            ClaimCore.Web.Composition.Runtime.openConnection
                                (connectionString database)
                                token

                        use runtime = configuredRuntime opened

                        use! holder = database.OwnerSource.OpenConnectionAsync()
                        use! transaction = holder.BeginTransactionAsync()
                        let! _ = Transactions.head holder token

                        let running =
                            call runtime database.Fixture.Owner (fun actor ->
                                (Core.execute
                                    actor
                                    (OperationInput.Case(
                                        request
                                            40
                                            "synthetic-drain"
                                            0L
                                            (ClaimCore.Domain.UnvalidatedCaseAct.Open registration)
                                    ))
                                    (token)))

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
                            (VerificationSupport.position verified).Sequence
                            4L
                            "Admitted transaction still committed exactly once"
                    }))
            testCase "unsupported major and minor metadata have the shared runtime fault" (fun () ->
                Admission.serverVersion (System.Version(18, 6))

                for version in
                    [ System.Version(18, 5); System.Version(17, 9); System.Version(19, 0) ] do
                    let mutable result = None

                    try
                        Admission.serverVersion version
                    with AdmissionRefusal fault ->
                        result <- Some fault

                    Expect.equal
                        result
                        (Some ClaimCore.Application.RuntimeOpenFault.UnsupportedServer)
                        "One admission vocabulary")
            testCaseAsync "plain cursor positions survive runtime lifetimes" (run runtimeCursors)
        ]
