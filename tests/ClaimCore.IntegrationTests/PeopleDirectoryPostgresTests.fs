module ClaimCore.IntegrationTests.PeopleDirectoryPostgresTests

open Expecto
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private scenario action (database: PostgresFixture.Database) =
    task {
        do! action database.Fixture
        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "bounded People directory"
        [
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
