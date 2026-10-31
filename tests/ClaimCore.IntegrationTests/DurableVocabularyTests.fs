module ClaimCore.IntegrationTests.DurableVocabularyTests

open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private corruptRead (read: unit -> Task<unit>) =
    task {
        let mutable rejected = false

        try
            do! read ()
        with :? RecordCorruptException ->
            rejected <- true

        Expect.isTrue
            rejected
            "An unknown permanent token must raise RecordCorruptException on read"
    }

let private readEntry (tx: IReadTx) =
    task {
        let! _ = tx.FindEntry(CaseFixtures.operationId (id 10))
        return ()
    }

let private readGrants (tx: IReadTx) =
    task {
        let! _ =
            tx.GrantPage
                {
                    ActorId = CaseFixtures.actorId (id 1)
                    AfterGrant = None
                    Limit = 50
                }

        return ()
    }

let private readActors (tx: IReadTx) =
    task {
        let! _ = tx.ActorPage { AfterActorId = None; Limit = 50 }
        return ()
    }

let private rawRead (database: PostgresFixture.Database) action =
    task {
        use! connection = database.RuntimeSource.OpenConnectionAsync()
        let! _ = action connection
        return ()
    }

let private rawEntry database =
    rawRead database (fun connection ->
        StoredEntries.operation
            connection
            database.Fixture.InstallationId
            (CaseFixtures.operationId (id 10))
            token)

let private rawGrants database =
    rawRead database (fun connection ->
        TransactionPages.grants connection (CaseFixtures.actorId (id 1)) None 50 token)

let private rawActors database =
    rawRead database (fun connection -> TransactionPages.actors connection None 50 token)

let private rejectsUnknown mutate read raw =
    run (fun database ->
        task {
            let! _ = populated database
            do! mutate database
            do! corruptRead (fun () -> raw database)

            let! boundary =
                database.Fixture.Store.Read
                    {
                        Work = read
                        CancellationToken = token
                    }

            Expect.equal
                boundary
                (Error StoreFailure.Integrity)
                "The public store boundary reports integrity failure"

            let! result = Verification.verify database.OwnerSource None token

            match result with
            | VerificationResult.Diverged _ -> ()
            | VerificationResult.Verified _ ->
                failtest "Unknown permanent token must produce DIVERGED"
        })

[<Tests>]
let tests =
    testList
        "unknown durable vocabularies"
        [
            testCaseAsync
                "case act rejects a well-shaped unknown token"
                (rejectsUnknown
                    (fun database ->
                        guardedSql
                            database
                            "UPDATE claimcore.ledger SET act='SYNTHETIC_UNKNOWN' WHERE sequence=4"
                            [||])
                    readEntry
                    rawEntry)
            testCaseAsync
                "role rejects a well-shaped unknown token"
                (rejectsUnknown
                    (fun database ->
                        sql
                            database
                            "INSERT INTO claimcore.grants(actor_id,role) VALUES ('80000000-0000-4000-8000-000000000001','SYNTHETIC_UNKNOWN')"
                            [||])
                    readGrants
                    rawGrants)
            testCaseAsync
                "principal kind rejects a well-shaped unknown token"
                (rejectsUnknown
                    (fun database ->
                        sql
                            database
                            "UPDATE claimcore.actors SET principal_kind='SYNTHETIC_UNKNOWN' WHERE actor_id='80000000-0000-4000-8000-000000000001'"
                            [||])
                    readActors
                    rawActors)
        ]
