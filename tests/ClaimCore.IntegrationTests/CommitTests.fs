module ClaimCore.IntegrationTests.CommitTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private token = CancellationToken.None

let private replay (database: PostgresFixture.Database) (actor: IActorClaimsCore) operation landed =
    task {
        let! replay = actor.Execute(operation, token)

        if landed then
            Expect.isTrue
                (match replay with
                 | ExecuteOutcome.AlreadyAccepted _ -> true
                 | _ -> false)
                "Commit landed exactly once"
        else
            accepted replay |> ignore

        let! head = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position head).Sequence
            4L
            "One accepted case operation after replay"
    }

let private proxyCore (database: PostgresFixture.Database) source =
    let store =
        LedgerStore(source, database.Fixture.InstallationId, Transactions.timeouts) :> ILedgerStore

    let actor =
        Core.create
            database.Fixture.InstallationId
            store
            database.Fixture.Calendar
            cursorKey
            database.Fixture.Owner

    actor

let private terminate (database: PostgresFixture.Database) backendId =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT pg_terminate_backend($1)"
                [| box backendId |]
                token

        Expect.isTrue
            (DatabaseSession.value<bool> 0 rows.Head)
            "Only the fixture's proxied backend is terminated"
    }

let private repeatedInitialization (database: PostgresFixture.Database) =
    task {
        let! id = SchemaAdministration.initialize database.OwnerSource "Etc/UTC" token

        let! repeated = SchemaAdministration.initialize database.OwnerSource "Etc/UTC" token

        Expect.equal repeated id "Repetition preserves the landed or freshly installed identity"
    }

[<Tests>]
let tests =
    testList
        "real commit connection loss"
        [
            for drop, landed in
                [
                    CommitProxy.Drop.BeforeCommit, false
                    CommitProxy.Drop.AfterCommit, true
                    CommitProxy.Drop.AfterCommitFatal, true
                    CommitProxy.Drop.Never, true
                ] do
                testCaseAsync
                    ($"replay after {drop}")
                    (run (fun database ->
                        task {
                            let! actor = caseWorker database.Fixture

                            let settings =
                                NpgsqlConnectionStringBuilder(
                                    database.RuntimeSource.ConnectionString
                                )

                            use proxy =
                                new CommitProxy.Proxy(
                                    nonNull settings.Host,
                                    settings.Port,
                                    drop,
                                    terminate = terminate database
                                )

                            settings.Password <- "synthetic-only"
                            settings.Host <- "127.0.0.1"
                            settings.Port <- proxy.Port
                            settings.SslMode <- SslMode.Disable
                            settings.GssEncryptionMode <- GssEncryptionMode.Disable
                            settings.Pooling <- false
                            use source = NpgsqlDataSource.Create settings.ConnectionString

                            let throughProxy = proxyCore database source

                            let operation =
                                request
                                    20
                                    "synthetic-commit"
                                    0L
                                    (ClaimCore.Domain.BusinessCommand.Open registration)

                            let! result = throughProxy.Execute(operation, token)

                            if drop = CommitProxy.Drop.Never then
                                accepted result |> ignore
                            else
                                do! proxy.Intercepted.WaitAsync(TimeSpan.FromSeconds 3.)

                                Expect.isTrue
                                    (match result with
                                     | ExecuteOutcome.OutcomeUnknown(id, _) ->
                                         id = operation.OperationId
                                     | _ -> false)
                                    "Lost commit reply stays unknown"

                                Expect.equal
                                    (Transactions.failure false (IO.IOException()))
                                    StoreFailure.Unavailable
                                    "Misclassifying as pre-commit fails the control"

                            if drop = CommitProxy.Drop.AfterCommitFatal then
                                Expect.equal
                                    proxy.FatalSqlState
                                    (Some "57P01")
                                    "The server sent a real fatal SQLSTATE after committing"

                            do! replay database actor operation landed
                        }))
            for drop in
                [
                    CommitProxy.Drop.BeforeCommit
                    CommitProxy.Drop.AfterCommit
                    CommitProxy.Drop.AfterCommitFatal
                ] do
                testCaseAsync
                    ($"initializer is repeatable after {drop}")
                    (run (fun database ->
                        task {
                            use! connection = database.OwnerSource.OpenConnectionAsync()

                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    "DROP SCHEMA claimcore CASCADE"
                                    [||]
                                    token

                            let settings =
                                NpgsqlConnectionStringBuilder(
                                    database.Container.GetConnectionString()
                                )

                            use proxy =
                                new CommitProxy.Proxy(
                                    nonNull settings.Host,
                                    settings.Port,
                                    drop,
                                    terminate = terminate database
                                )

                            settings.Host <- "127.0.0.1"
                            settings.Port <- proxy.Port
                            settings.SslMode <- SslMode.Disable
                            settings.GssEncryptionMode <- GssEncryptionMode.Disable
                            settings.Pooling <- false
                            use administration = new Administration(settings.ConnectionString)
                            let mutable unknown = false

                            try
                                let! _ = administration.Initialize("Etc/UTC", token) in ()
                            with :? AdministrationOutcomeUnknownException ->
                                unknown <- true

                            if drop = CommitProxy.Drop.AfterCommitFatal then
                                Expect.equal
                                    proxy.FatalSqlState
                                    (Some "57P01")
                                    "Real fatal owner commit response"

                            Expect.isTrue
                                unknown
                                "Lost owner commit reply has an explicit unknown outcome"

                            do! repeatedInitialization database
                        }))
            testCase "a SQLSTATE is not by itself proof that a dispatched commit aborted" (fun () ->
                for severity, state in
                    [ "FATAL", "57P01"; "PANIC", "XX000"; "ERROR", "08007"; "ERROR", "40003" ] do
                    let error =
                        PostgresException(
                            "Synthetic transport uncertainty",
                            severity,
                            severity,
                            state
                        )

                    Expect.equal
                        (Transactions.failure true error)
                        StoreFailure.CommitOutcomeUnknown
                        "No false definite failure"

                    Expect.equal
                        (Transactions.failure false error)
                        StoreFailure.Unavailable
                        "Before commit dispatch is definite"

                for state in [ "23514"; "40001"; "P0001" ] do
                    let error =
                        PostgresException("Synthetic transaction abort", "ERROR", "ERROR", state)

                    Expect.equal
                        (Transactions.failure true error)
                        StoreFailure.Unavailable
                        "Established transaction abort is definite")
        ]
