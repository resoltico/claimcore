module ClaimCore.IntegrationTests.StoredDataFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs
open ClaimCore.IntegrationTests.LedgerFindingScenarios
open ClaimCore.IntegrationTests.AuthorityFindingScenarios


let internal malformedProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.actors SET issuer='not-an-issuer' WHERE actor_id=$1"
                [| box (id 1) |]

        do! finding VerificationFinding.AuthorityProjection database
    }

let internal malformedInstant (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "ALTER TABLE claimcore.ledger DROP CONSTRAINT ledger_recorded_at_check"
                [||]

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET recorded_at='infinity' WHERE sequence=4"
                [||]

        do! finding VerificationFinding.PayloadFormat database
    }

let internal malformedOptionalDate (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let decision: DecisionInput =
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = "1"
                PayableCurrency = "EUR"
            }

        let! decided =
            (Service.execute
                actor
                (OperationInput.Case(request 11 "synthetic-a" 1L (AuthoredCaseAct.Decide decision)))
                (token))

        accepted decided |> ignore
        let! _ = verified database

        do!
            sql
                database
                "ALTER TABLE claimcore.cases DROP CONSTRAINT cases_payment_date_check; UPDATE claimcore.cases SET payment_date='infinity'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let internal malformedAmount (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money; UPDATE claimcore.cases SET claimed_amount='NaN'"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let internal ioFailure (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! database.Container.StopAsync()
        let mutable failure = None

        try
            let! _ = Verification.verify database.OwnerSource None token in ()
        with error ->
            failure <- Some(AdministrationFailures.classify error)

        Expect.equal
            failure
            (Some AdministrationFailure.Failed)
            "Unavailable database stays an I/O failure, not divergence"
    }

let internal duplicateIdentity earlierFailure occurrences (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! closed =
            (Service.execute
                actor
                (OperationInput.Case(request 11 "synthetic-a" 1L AuthoredCaseAct.Close))
                (token))

        accepted closed |> ignore

        let! reopened =
            (Service.execute
                actor
                (OperationInput.Case(request 12 "synthetic-a" 2L AuthoredCaseAct.Reopen))
                (token))

        accepted reopened |> ignore
        do! sql database "ALTER TABLE claimcore.ledger DROP CONSTRAINT ledger_operation_id_key" [||]

        for sequence in [ 5L .. int64 (3 + occurrences) ] do
            do!
                rewrite database sequence (fun value ->
                    let request, snapshot = livePayload value

                    { value with
                        Metadata =
                            { value.Metadata with
                                OperationId = id 10
                            }
                    }
                    |> updateCase { request with OperationId = id 10 } snapshot)

        match earlierFailure with
        | Some sequence ->
            do!
                guardedSql
                    database
                    "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=$1"
                    [| box sequence |]
        | None -> ()

        let! result = Verification.verify database.OwnerSource None token
        let sequence = earlierFailure |> Option.defaultValue 5L

        let expected =
            if earlierFailure.IsSome then
                VerificationFinding.EntryHash
            else
                VerificationFinding.PayloadFormat

        Expect.equal
            result
            (VerificationResult.Diverged
                {
                    Sequence = Some sequence
                    Finding = expected
                })
            "Earliest second occurrence is checked after hash and payload, before history"
    }
