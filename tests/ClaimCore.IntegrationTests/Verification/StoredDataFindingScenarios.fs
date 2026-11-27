module ClaimCore.IntegrationTests.StoredDataFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs
open ClaimCore.IntegrationTests.LedgerFindingScenarios
open ClaimCore.IntegrationTests.PeopleFindingScenarios


let internal malformedProjection (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            sql
                database
                "UPDATE claimcore.actors SET issuer='not-an-issuer' WHERE actor_id=$1"
                [| box (id database.Fixture 1) |]

        do! finding VerificationFinding.PeopleProjection database
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
            (ClaimCore.TestSupport.DomainInputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, ("2026-08-15")
                    BusinessField.PayableAmount, ("1")
                    BusinessField.PayableCurrency, ("EUR")
                ])

        let! decided =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (11)
                    ("synthetic-a")
                    (1L)
                    "case.decide"
                    (ClaimCore.TestSupport.Submissions.fields (DecisionInput.values (decision))))))
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
            let! _ = Verification.verify database.OwnerSource database.MasterKey None token in ()
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
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (11)
                    ("synthetic-a")
                    (1L)
                    "case.close"
                    (CaseTransitionInputs.closed))))
                (token))

        accepted closed |> ignore

        let! reopened =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (12)
                    ("synthetic-a")
                    (2L)
                    "case.reopen"
                    (CaseTransitionInputs.reopened))))
                (token))

        accepted reopened |> ignore
        // These projections reference creation operations. The isolated corruption fixture
        // removes their dependent keys before introducing duplicate ledger identities.
        do!
            sql
                database
                """ALTER TABLE claimcore.digest_runs DROP CONSTRAINT digest_runs_actor_id_fkey;
                   ALTER TABLE claimcore.credential_publications DROP CONSTRAINT credential_publications_actor_id_fkey;
                   ALTER TABLE claimcore.ledger DROP CONSTRAINT ledger_operation_id_key"""
                [||]

        for sequence in [ 5L .. int64 (3 + occurrences) ] do
            do!
                rewrite database (CaseFixtures.sequence sequence) (fun value ->
                    let request, snapshot =
                        livePayload (CaseFixtures.reference "synthetic-a") value

                    updateCase
                        (Submissions.withOperationId (id database.Fixture 10) request)
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

        let! result = Verification.verify database.OwnerSource database.MasterKey None token
        // Loading a root by its now-ambiguous identity must refuse at the first affected root.
        let sequence = earlierFailure |> Option.map (min 4L) |> Option.defaultValue 4L
        let expected = VerificationFinding.PayloadFormat

        Expect.equal
            result
            (VerificationResult.Diverged
                {
                    Sequence = Some(CaseFixtures.sequence (sequence))
                    Finding = expected
                    Attachment = None
                })
            "Ambiguous accepted identities refuse at the first affected root; earlier corruption retains precedence"
    }
