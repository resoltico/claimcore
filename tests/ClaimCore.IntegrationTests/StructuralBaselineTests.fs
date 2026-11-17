module ClaimCore.IntegrationTests.StructuralBaselineTests

open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private malformedPerson (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        let! _ = verified database
        // Prove the append-only guard before the owned-fixture corruption bypass.
        do!
            guardedSql
                database
                "UPDATE claimcore.ledger_effects SET retained=retained || '{\"unknownMember\":null}'::jsonb WHERE operation_sequence=1 AND effect_index=0"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

let private unknownTask (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        // SQL admits a well-formed token; the task registry owns the semantic inventory.
        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET task='case.unsupported' WHERE aggregate_kind='FAMILY'"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

[<Tests>]
let tests =
    testList
        "structural SQL baseline"
        [
            testCaseAsync
                "privileged malformed person part metadata is rejected by verification"
                (run malformedPerson)
            testCaseAsync "privileged unknown task is rejected by verification" (run unknownTask)
        ]
