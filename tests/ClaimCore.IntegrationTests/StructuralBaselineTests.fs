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
        // Prove the append-only guard before the owned-fixture corruption bypass.
        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET command=jsonb_set(command,'{act,kind}','\"ADMIT\"'::jsonb) WHERE sequence=1"
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
                "UPDATE claimcore.ledger SET command=jsonb_set(command,'{act}','\"unsupported!\"'::jsonb) WHERE command->>'kind'='CASE'"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "structural SQL baseline"
        [
            testCaseAsync
                "privileged malformed ADMIT metadata is rejected by verification"
                (run malformedAdmission)
            testCaseAsync "privileged unknown act is rejected by verification" (run unknownAct)
        ]
