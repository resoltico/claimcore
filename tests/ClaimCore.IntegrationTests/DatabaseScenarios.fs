module ClaimCore.IntegrationTests.DatabaseScenarios

open ClaimCore.TestSupport

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures


let internal token = CancellationToken.None

let internal run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let internal sql (database: PostgresFixture.Database) text =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection text [||] token
        return ()
    }

let internal refused action message =
    task {
        let mutable refused = false

        try
            let! _ = action () in ()
        with _ ->
            refused <- true

        Expect.isTrue refused message
    }

let internal populated (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! outcome =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 10 "synthetic-a" 0L (ClaimCore.Domain.AuthoredCaseAct.Open registration)
                ))
                (token))

        let receipt = accepted outcome

        let! second =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 11 "synthetic-b" 0L (ClaimCore.Domain.AuthoredCaseAct.Open registration)
                ))
                (token))

        accepted second |> ignore

        return actor, receipt
    }

let internal verify (database: PostgresFixture.Database) =
    Verification.verify database.OwnerSource None token

let internal erasureFixture (database: PostgresFixture.Database) =
    task {
        let! actor, _ = populated database

        let! granted =
            grant
                actor
                (id 12)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        return actor
    }

let internal erased (actor: ServiceContext) =
    task {
        let request = actRequest 13 "synthetic-a" 1L (AuthoredCaseAct.Bare BareAct.Erase)

        let! result = (Service.execute actor (OperationInput.Case request) (token))

        return accepted result
    }

let internal position =
    function
    | VerificationResult.Verified position -> position
    | VerificationResult.Diverged _ -> failtest "Expected consistent ledger"

let internal diverged code =
    function
    | VerificationResult.Diverged finding ->
        Expect.equal finding.Finding code "Exact verification divergence"
    | _ -> failtest "Expected typed divergence"

let internal exec (database: PostgresFixture.Database) arguments =
    task {
        let! result = database.Container.ExecAsync arguments

        Expect.equal
            result.ExitCode
            (Nullable 0L)
            "Synthetic database backup/restore command succeeds"
    }

let internal concurrentInitialization (database: PostgresFixture.Database) =
    task {
        do! sql database "DROP SCHEMA claimcore CASCADE"
        use! blocker = database.OwnerSource.OpenConnectionAsync()
        use! transaction = blocker.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                blocker
                "SELECT pg_advisory_xact_lock(4849322681855856466)"
                [||]
                token

        let attempts =
            [| for _ in 1..8 -> Baseline.initialize database.OwnerSource "Etc/UTC" token |]

        let mutable waiting = false
        let mutable namespacePresent = false

        try
            for _ in 1..100 do
                if not waiting then
                    let! rows =
                        DatabaseSession.rows
                            blocker
                            "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())"
                            [||]
                            token

                    waiting <- DatabaseSession.value<int64> 0 rows.Head = 8L

                    if not waiting then
                        do! System.Threading.Tasks.Task.Delay 50

            let! present = Baseline.exists blocker token
            namespacePresent <- present
        finally
            transaction.Rollback()

        let! ids = System.Threading.Tasks.Task.WhenAll attempts
        Expect.isTrue waiting "Every initializer waits before namespace discovery"
        Expect.isFalse namespacePresent "No installation work precedes the administration lock"
        Expect.equal (ids |> Array.distinct |> Array.length) 1 "One immutable installation"
        let! installation = InstallationCheck.openRuntime database.RuntimeSource token

        Expect.equal
            installation.InstallationId
            ids[0]
            "Admitted tuple agrees with every initializer"
    }

let internal initializationRollback (database: PostgresFixture.Database) =
    task {
        do! sql database "DROP SCHEMA claimcore CASCADE; DROP ROLE claimcore_runtime"

        do!
            refused
                (fun () -> Baseline.initialize database.OwnerSource "Etc/UTC" token)
                "Missing runtime role makes the baseline fail after DDL"

        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! present = Baseline.exists connection token
        Expect.isFalse present "Failed installation leaves no namespace or identity"
        do! sql database "CREATE ROLE claimcore_runtime LOGIN"
        let! _ = Baseline.initialize database.OwnerSource "Etc/UTC" token
        let! _ = Baseline.require connection token
        return ()
    }
