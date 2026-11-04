module ClaimCore.IntegrationTests.AdmitPostgresTests

open Expecto
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private withVerifiedLedger scenario (database: PostgresFixture.Database) =
    task {
        do! scenario database.Fixture
        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "atomic authority admission"
        [
            testCaseAsync
                "identity profile distinct scope effects and erased acceptance remain verifiable"
                (run (withVerifiedLedger AdmitContract.lifecycle))
            testCaseAsync
                "normalized duplicates and role boundaries leave no partial actor"
                (run (withVerifiedLedger AdmitContract.boundary))
            testCaseAsync
                "maximum48 grants use50 fresh effect nonces and a final proof"
                (run (withVerifiedLedger AdmitContract.maximum))
        ]
