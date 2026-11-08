module ClaimCore.IntegrationTests.StructuralBaselineTests

open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private malformedAdmission (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        let! _ = verified database
        // The append-only guard remains independent; the owner corruption path leaves CHECKs enabled.
        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET authority_operation='ADMIT' WHERE operation_id=(SELECT operation_id FROM claimcore.ledger WHERE sequence=1)"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

let private unknownAct (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        // This is deliberately outside the retired uppercase act regex, not just an unused known token.
        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET act='unsupported!' WHERE subject_kind='CASE'"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "structural SQL baseline"
        [
            testCaseAsync
                "SQL admits malformed ADMIT metadata and verification rejects it"
                (run malformedAdmission)
            testCaseAsync "SQL admits an unknown act and verification rejects it" (run unknownAct)
        ]
