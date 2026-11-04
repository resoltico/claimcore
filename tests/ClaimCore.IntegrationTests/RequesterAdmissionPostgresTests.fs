module ClaimCore.IntegrationTests.RequesterAdmissionPostgresTests

open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private scenario action (database: PostgresFixture.Database) =
    task {
        do! action database.Fixture
        let! _ = verified database
        return ()
    }

let private writeSnapshot (database: PostgresFixture.Database) =
    task {
        let! _ = caseWorker database.Fixture
        let! before = verified database

        let unknown =
            PrincipalKey.human "https://issuer.example/" "unknown-refusal-lock"
            |> Result.defaultWith (string >> failtest)

        let caller = core database.Fixture unknown
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                connection
                "SELECT sequence FROM claimcore.ledger_head FOR UPDATE"
                [||]
                token

        let pending =
            Service.execute caller RequesterAdmissionContract.malformedWrites.Head token

        let mutable blocked = false

        try
            for _ in 1..40 do
                if not blocked then
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%lock_ledger_head%')"
                            [||]
                            token

                    blocked <- DatabaseSession.value<bool> 0 rows.Head

                    if not blocked then
                        do! Task.Delay 25
        finally
            transaction.Rollback()

        let! reply = pending

        Expect.isTrue
            blocked
            "A malformed write owns the existing head transaction before caller-state refusal"

        Expect.equal
            reply.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "The released write snapshot still refuses the unknown caller"

        let! after = verified database

        Expect.equal
            after
            before
            "The refused write appended no effect, control, profile or projection"
    }

[<Tests>]
let tests =
    testList
        "requester input refusal snapshots"
        [
            testCaseAsync
                "unavailable callers hide early business input details"
                (run (scenario RequesterAdmissionContract.lifecycle))
            testCaseAsync
                "registered target gates retain scope and capability distinctions"
                (run (scenario RequesterAdmissionContract.targetPrivacy))
            testCaseAsync
                "refused malformed writes use the existing head without ledger effects"
                (run writeSnapshot)
        ]
