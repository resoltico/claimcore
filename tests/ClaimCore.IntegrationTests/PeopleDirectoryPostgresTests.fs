module ClaimCore.IntegrationTests.PeopleDirectoryPostgresTests

open Expecto
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private scenario action (database: PostgresFixture.Database) =
    task {
        do! action database.Fixture
        let! _ = verified database
        return ()
    }

let private nameProjection (database: PostgresFixture.Database) =
    task {
        do! ActorNameSearchContract.lookup database.Fixture
        let! before = checkpoint database

        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name_search='wrong normalized projection' WHERE actor_id=$1"
                [| box (ClaimCore.TestSupport.StoreFixtures.id database.Fixture 1100) |]

        do!
            finding
                ClaimCore.Application.Contracts.VerificationFinding.AuthorityProfileProjection
                database

        do! enterMaintenance database
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource before token

        Expect.isOk
            rebuilt
            "Name search projection rebuilds only from retained current profile meaning"

        let! after = verified database
        Expect.equal after before.Position "Rebuilding the name index changes no retained proof"
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT display_name,display_name_search FROM claimcore.actors WHERE actor_id=$1"
                [| box (ClaimCore.TestSupport.StoreFixtures.id database.Fixture 1100) |]
                token

        Expect.equal
            (DatabaseSession.value<string> 1 rows.Head)
            "renamed person"
            "The obsolete Alice search value cannot survive a reprofile/rebuild"

        let! _ = DatabaseSession.execute connection "SET enable_seqscan=off" [||] token

        let! plan =
            DatabaseSession.rows
                connection
                "EXPLAIN SELECT actor_id FROM claimcore.actors WHERE display_name_search LIKE '%renamed%'"
                [||]
                token

        let lines = plan |> List.map (DatabaseSession.value<string> 0) |> String.concat "\n"

        Expect.stringContains
            lines
            "actors_display_name"
            "The existing bounded trigram index supports the literal normalized-name predicate"

    }

let private contactProjection (database: PostgresFixture.Database) =
    task {
        do! ActorNameSearchContract.contactAndRole database.Fixture
        let! before = checkpoint database

        do!
            sql
                database
                "UPDATE claimcore.actors SET contact_search='stale contact' WHERE actor_id=$1"
                [| box (ClaimCore.TestSupport.StoreFixtures.id database.Fixture 1140) |]

        do!
            finding
                ClaimCore.Application.Contracts.VerificationFinding.AuthorityProfileProjection
                database

        do! enterMaintenance database
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource before token
        Expect.isOk rebuilt "Contact lookup projection rebuilds from the current retained profile"
        let! after = verified database
        Expect.equal after before.Position "Contact rebuild preserves retained proof"
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection "SET enable_seqscan=off" [||] token

        let! rows =
            DatabaseSession.rows
                connection
                "EXPLAIN SELECT actor_id FROM claimcore.actors WHERE contact_search LIKE '%new@example%'"
                [||]
                token

        let plan = rows |> List.map (DatabaseSession.value<string> 0) |> String.concat "\n"

        Expect.stringContains
            plan
            "actors_contact"
            "Contact literal lookup has bounded trigram index support"
    }

[<Tests>]
let tests =
    testList
        "bounded People directory"
        [
            testCaseAsync
                "contact search projection corruption rebuild and index are verified"
                (run contactProjection)
            testCaseAsync
                "directory contact and typed role predicates erase and reprofile together"
                (run (scenario ActorNameSearchContract.contactAndRole))
            testCaseAsync
                "normalized name projection divergence and rebuild preserve proof"
                (run nameProjection)
            testCaseAsync
                "Domain-normalized current-name search preserves literal Unicode semantics"
                (run (scenario ActorNameSearchContract.lookup))
            testCaseAsync
                "all7 roles retain bounded deterministic summaries"
                (run (scenario PeopleDirectoryContract.maximumRoles))
            testCaseAsync
                "role scope summaries and disabled target review share the snapshot"
                (run (scenario PeopleDirectoryContract.summaries))
            testCaseAsync
                "raw assignment state leaves service authorization vetoes intact and repairable"
                (run (scenario PeopleDirectoryContract.corruptAssignment))
        ]
