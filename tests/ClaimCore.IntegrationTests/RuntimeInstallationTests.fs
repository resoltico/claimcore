module ClaimCore.IntegrationTests.RuntimeInstallationTests

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

/// Raw transport relay: future connections genuinely reach another admitted database.
type private RetargetingProxy(host: string, port: int) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let stop = new CancellationTokenSource()
    let mutable target = host, port
    let connections = Collections.Generic.List<Task>()
    do listener.Start()

    let loop =
        task {
            try
                while not stop.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync(stop.Token)
                    let destination = target

                    let relay =
                        task {
                            use client = client
                            use server = new TcpClient()

                            try
                                let host, port = destination
                                do! server.ConnectAsync(host, port, stop.Token)
                                use incoming = client.GetStream()
                                use outgoing = server.GetStream()
                                let first = incoming.CopyToAsync(outgoing, stop.Token)
                                let second = outgoing.CopyToAsync(incoming, stop.Token)
                                let! _ = Task.WhenAny(first, second)
                                client.Close()
                                server.Close()

                                try
                                    do! Task.WhenAll(first, second)
                                with _ ->
                                    ()
                            with _ ->
                                ()
                        }

                    connections.Add relay
            with :? OperationCanceledException ->
                ()
        }

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Retarget(host, port) = target <- host, port

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! stop.CancelAsync()
                    listener.Stop()
                    do! loop
                    do! Task.WhenAll connections
                    stop.Dispose()
                }
            )

let private token = CancellationToken.None

let private connection (database: PostgresFixture.Database) =
    let value = NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)
    value.Password <- "synthetic-only"
    value.Pooling <- false
    value

let private refused action =
    task {
        let mutable failed = false

        try
            do! action ()
        with :? InstallationConfigurationException ->
            failed <- true

        Expect.isTrue failed "Foreign installation is refused before operational access"
    }

let private retarget () =
    task {
        let! first = PostgresFixture.create ()
        let mutable firstDisposed = false

        try
            let! _ = bootstrap first.Fixture
            let settings = connection first
            use proxy = new RetargetingProxy(nonNull settings.Host, settings.Port)
            settings.Host <- "127.0.0.1"
            settings.Port <- proxy.Port
            let! opened = Runtime.openConnection settings.ConnectionString token

            use runtime =
                opened
                |> Result.defaultWith (fun fault -> failtestf "Runtime admission: %A" fault)

            let store = Runtime.store runtime
            let! ready = runtime.Ready token
            Expect.isTrue ready "Matching positive control"
            first.Fixture.Dispose()
            firstDisposed <- true
            let! second = PostgresFixture.create ()

            try
                let! _ = bootstrap second.Fixture
                let target = connection second
                proxy.Retarget(nonNull target.Host, target.Port)
                let! ready = runtime.Ready token
                Expect.isFalse ready "Retargeting never reports ready"

                let! read =
                    store.Read
                        {
                            Work = (fun tx -> PeopleReads.actorCount tx ())
                            CancellationToken = token
                        }

                Expect.equal
                    read
                    (Error StoreFailure.SchemaMismatch)
                    "Same registered principal cannot read B"

                let bound =
                    { core first.Fixture first.Fixture.Owner with
                        Store = store
                    }

                let! absent =
                    Service.operationGet
                        bound
                        {
                            OperationId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-2.))
                        }
                        token

                Expect.equal
                    absent.Outcome
                    (Outcome.Failed CoreFault.SchemaMismatch)
                    "Foreign storage cannot establish final operation absence"

                let! context =
                    store.Context
                        {
                            Principal =
                                PrincipalKey.human "https://issuer.example/" "unknown"
                                |> Result.defaultWith (fun _ -> failtest "principal")
                            Claims = RequesterClaims.empty
                            CancellationToken = token
                        }

                Expect.equal
                    (context |> Result.map ignore)
                    (Error StoreFailure.SchemaMismatch)
                    "No foreign access request"

                let correct =
                    SessionStore(second.RuntimeSource, second.Installation) :> ISessionStore

                let! session =
                    correct.Create(
                        SessionResults.principal,
                        [| 1uy |],
                        None,
                        TimeSpan.FromMinutes 5.,
                        TimeSpan.FromHours 1.,
                        token
                    )

                do!
                    refused (fun () ->
                        task {
                            let! _ = runtime.Sessions.Retrieve(session.Value, token) in return ()
                        })

                do!
                    refused (fun () ->
                        task {
                            let! _ =
                                runtime.Sessions.Create(
                                    SessionResults.principal,
                                    [| 2uy |],
                                    None,
                                    TimeSpan.FromMinutes 5.,
                                    TimeSpan.FromHours 1.,
                                    token
                                ) in

                            return ()
                        })

                do!
                    refused (fun () ->
                        task {
                            let! _ =
                                runtime.Sessions.Renew(
                                    session.Value,
                                    SessionResults.principal,
                                    [| 2uy |],
                                    (fun _ -> true),
                                    token
                                ) in

                            return ()
                        })

                do!
                    refused (fun () ->
                        task {
                            let! _ =
                                runtime.Sessions.Remove(
                                    session.Value,
                                    SessionEndReason.Logout,
                                    token
                                ) in

                            return ()
                        })

                use! seed = second.OwnerSource.OpenConnectionAsync()

                let! _ =
                    DatabaseSession.execute
                        seed
                        "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key) VALUES(repeat('a',64),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800,repeat('b',64))"
                        [||]
                        token

                let! _ =
                    DatabaseSession.execute
                        seed
                        "INSERT INTO claimcore.access_request(principal_key,issuer,principal_kind,subject,first_seen,last_seen) VALUES($1,'https://issuer.example/','HUMAN','expired-foreign',clock_timestamp()-interval '40 days',clock_timestamp()-interval '35 days')"
                        [|
                            box (
                                StoreFixtures.humanPrincipalKey
                                    "https://issuer.example/"
                                    "expired-foreign"
                            )
                        |]
                        token

                do!
                    refused (fun () ->
                        task {
                            let! _ =
                                runtime.Housekeep(
                                    (fun instant ->
                                        System.DateOnly.FromDateTime instant.UtcDateTime),
                                    token
                                ) in

                            return ()
                        })

                let! retained = correct.Retrieve(session.Value, token)

                Expect.equal
                    (retained.Value |> Option.map _.Payload)
                    (Some [| 1uy |])
                    "No foreign renewal or removal"

                use! check = second.OwnerSource.OpenConnectionAsync()

                let! rows =
                    DatabaseSession.rows
                        check
                        "SELECT count(*) FILTER(WHERE subject='unknown'),count(*) FILTER(WHERE subject='expired-foreign') FROM claimcore.access_request"
                        [||]
                        token

                Expect.equal
                    (DatabaseSession.value<int64> 0 rows.Head)
                    0L
                    "Unknown Context did not insert a foreign request"

                Expect.equal
                    (DatabaseSession.value<int64> 1 rows.Head)
                    1L
                    "Foreign housekeeping did not delete expired access requests"

                let! preserved =
                    DatabaseSession.rows
                        check
                        "SELECT (SELECT count(*) FROM claimcore.sessions WHERE session_key=repeat('a',64)),expired_before_ticks FROM claimcore.ledger_head WHERE singleton"
                        [||]
                        token

                Expect.equal
                    (DatabaseSession.value<int64> 0 preserved.Head)
                    1L
                    "Foreign housekeeping did not delete expired rows"

                Expect.isNone
                    (DatabaseSession.optional<int64> 1 preserved.Head)
                    "Foreign absence did not advance expiry"

                let settingsB = connection second
                let! openedB = Runtime.openConnection settingsB.ConnectionString token

                use runtimeB =
                    openedB |> Result.defaultWith (fun _ -> failtest "Matching B admission")

                let! _ =
                    DatabaseSession.execute
                        check
                        "UPDATE claimcore.installation SET business_time_zone='Europe/Riga'"
                        [||]
                        token

                let! zoneReady = runtimeB.Ready token

                Expect.isFalse
                    zoneReady
                    "A zone-only change fails readiness despite the same installation ID"

                let! zoneRead =
                    (Runtime.store runtimeB).Read
                        {
                            Work = (fun tx -> PeopleReads.actorCount tx ())
                            CancellationToken = token
                        }

                Expect.equal
                    zoneRead
                    (Error StoreFailure.SchemaMismatch)
                    "The zone is part of every runtime transaction admission"
            finally
                second.Fixture.Dispose()
        finally
            if not firstDisposed then
                first.Fixture.Dispose()
    }

let private retargetConfirmation () =
    task {
        let! first = PostgresFixture.create ()

        try
            let! _ = bootstrap first.Fixture
            let! second = PostgresFixture.create ()

            try
                let! _ = bootstrap second.Fixture
                let settings = connection first
                use proxy = new RetargetingProxy(nonNull settings.Host, settings.Port)
                settings.Host <- "127.0.0.1"
                settings.Port <- proxy.Port
                let! admitted = Runtime.openConnection settings.ConnectionString token

                use runtime =
                    admitted |> Result.defaultWith (fun fault -> failtestf "Admission: %A" fault)

                let inner = Runtime.store runtime
                let destination = connection second
                let mutable reads = 0
                let mutable writes = 0

                let wrapper =
                    { new IStore with
                        member _.Context query = inner.Context query

                        member _.Read operation =
                            task {
                                let! result = inner.Read operation
                                reads <- reads + 1

                                Expect.isOk
                                    result
                                    "Initial operation absence is genuinely read from A"

                                proxy.Retarget(nonNull destination.Host, destination.Port)
                                return result
                            }

                        member _.Write operation =
                            writes <- writes + 1
                            inner.Write operation
                    }

                let bound =
                    { core first.Fixture first.Fixture.Owner with
                        Store = wrapper
                    }

                let frontier () =
                    task {
                        use! connection = second.OwnerSource.OpenConnectionAsync()

                        let! rows =
                            DatabaseSession.rows
                                connection
                                "SELECT expired_before_ticks FROM claimcore.ledger_head WHERE singleton"
                                [||]
                                token

                        return DatabaseSession.optional<int64> 0 rows.Head
                    }

                let! before = frontier ()

                let! absent =
                    Service.operationGet
                        bound
                        {
                            OperationId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-2.))
                        }
                        token

                Expect.equal reads 1 "One successful A read"

                Expect.equal
                    writes
                    1
                    "Final absence reaches the separate confirmation Write against B"

                let! after = frontier ()
                Expect.equal after before "B's frontier was independently inspected and unchanged"

                Expect.equal
                    absent.Outcome
                    (Outcome.Failed CoreFault.SchemaMismatch)
                    "Write admission independently refuses foreign confirmation"

                Expect.isNone
                    before
                    "The control would need to advance the frontier without the Write guard"
            finally
                second.Fixture.Dispose()
        finally
            first.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "runtime admitted installation"
        [
            testCaseAsync
                "A read followed by B final-absence confirmation refuses at Write"
                (async { do! retargetConfirmation () |> Async.AwaitTask })
            testCaseAsync
                "future physical connections retarget to another database without foreign reads or session changes"
                (async { do! retarget () |> Async.AwaitTask })
        ]
