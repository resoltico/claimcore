module ClaimCore.IntegrationTests.StoredIdentityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private qualify path (database: PostgresFixture.Database) =
    task {
        let! actor = bootstrap database.Fixture
        let operationId = id database.Fixture 2

        let! executed =
            grant
                actor
                operationId
                database.Fixture.Owner
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        let receipt = accepted executed

        let observedOperation =
            if path = [ "principal"; "kind" ] then
                id database.Fixture 1
            else
                operationId

        let! before = Service.operationGet actor { OperationId = observedOperation } token

        match before.Outcome with
        | Outcome.Succeeded _ -> ()
        | _ -> failtest "The exact receipt is readable before its stored enum is damaged"

        let sequence =
            if path = [ "principal"; "kind" ] then
                // The provisioning root owns its public identity and private profile compartment.
                CaseFixtures.sequence 1L
            else
                receipt.Position.Sequence

        do!
            guardedSql database
                "UPDATE claimcore.ledger SET command=jsonb_set(command,$2::text[],'\"UNDECLARED\"'::jsonb) WHERE sequence=$1"
                [| box (Sequence.value sequence); box (List.toArray path) |]

        let! after = Service.operationGet actor { OperationId = observedOperation } token

        Expect.equal
            after.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Stored corruption is never a user-input refusal"
    }

[<Tests>]
let tests =
    testList
        "stored identity enums"
        [
            testCaseAsync
                "a damaged stored principal kind is an integrity fault"
                (run (qualify [ "principal"; "kind" ]))
            testCaseAsync
                "a damaged stored role is an integrity fault"
                (run (qualify [ "act"; "role" ]))
        ]
