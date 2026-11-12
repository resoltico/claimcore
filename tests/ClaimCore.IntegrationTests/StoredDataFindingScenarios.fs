module ClaimCore.IntegrationTests.StoredDataFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
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
                [| box (id database.Fixture 1) |]

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
            (ClaimCore.TestSupport.Inputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, ("2026-08-15")
                    BusinessField.PayableAmount, ("1")
                    BusinessField.PayableCurrency, ("EUR")
                ])

        let! decided =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request database.Fixture 11 "synthetic-a" 1L (AuthoredCaseAct.Decide decision)
                ))
                (token))

        accepted decided |> ignore
        let! _ = verified database

        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=convert_to(replace(convert_from(snapshot,'UTF8'), '2026-08-15', 'infinity'),'UTF8')"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let internal malformedAmount (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=convert_to(replace(convert_from(snapshot,'UTF8'), '\"claimedAmount\":\"1000\"', '\"claimedAmount\":\"NaN\"'),'UTF8')"
                [||]

        do! finding VerificationFinding.CaseProjection database
    }

let internal ioFailure (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        do! database.Container.StopAsync()
        let mutable failure = None
        let mutable category = "no exception"

        try
            let! _ = Verification.verify database.OwnerSource None token in ()
        with error ->
            category <-
                error.GetType().Name
                + (match error with
                   | :? Npgsql.PostgresException as pg -> ":" + pg.SqlState
                   | _ -> "")

            failure <- Some(AdministrationFailures.classify error)

        Expect.equal
            failure
            (Some(AdministrationFailure.Fault OperatorFault.Unavailable))
            ("Unavailable database stays an I/O failure, not divergence; category="
             + category)
    }

let internal duplicateIdentity earlierFailure occurrences (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! closed =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        11
                        "synthetic-a"
                        1L
                        (AuthoredCaseAct.Bare BareAct.Close)
                ))
                (token))

        accepted closed |> ignore

        let! reopened =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        12
                        "synthetic-a"
                        2L
                        (AuthoredCaseAct.Bare BareAct.Reopen)
                ))
                (token))

        accepted reopened |> ignore
        do! sql database "ALTER TABLE claimcore.ledger DROP CONSTRAINT ledger_operation_id_key" [||]

        for sequence in [ 5L .. int64 (3 + occurrences) ] do
            do!
                rewrite database (CaseFixtures.sequence sequence) (fun value ->
                    let request, snapshot = livePayload value

                    updateCase
                        { request with
                            OperationId = id database.Fixture 10
                        }
                        snapshot
                        value)

        match earlierFailure with
        | Some sequence ->
            do!
                guardedSql
                    database
                    "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=$1"
                    [| box sequence |]
        | None -> ()

        let! result = Verification.verify database.OwnerSource None token
        // Loading a root by its now-ambiguous identity must refuse at the first affected root.
        let sequence = earlierFailure |> Option.map (min 4L) |> Option.defaultValue 4L
        let expected = VerificationFinding.PayloadFormat

        Expect.equal
            result
            (VerificationResult.Diverged
                {
                    Sequence = Some(CaseFixtures.sequence (sequence))
                    Finding = expected
                })
            "Ambiguous accepted identities refuse at the first affected root; earlier corruption retains precedence"
    }
