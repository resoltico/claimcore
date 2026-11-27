module ClaimCore.IntegrationTests.PeopleFindingScenarios

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application
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


let internal disabledExecutor (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let reader = target "disabled"
        do! register database.Fixture actor 3 reader

        let! disabled =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 4))
                        (reader)
                        (None)
                        "people.disable"
                        (Map.empty)

                return! Service.executeSubmission (actor) submission ((token))
            }))

        acceptedPeople disabled

        let! opened =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (11)
                    ("another")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (token))

        do!
            rewrite
                database
                (accepted opened).Position.Sequence
                (VerificationScenarioInputs.executor (CaseFixtures.actorId (id database.Fixture 3)))

        do! finding VerificationFinding.ActorNotEnabled database
    }

let internal serviceGrantTarget (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let service =
            PrincipalKey.service "https://issuer.example/" "service"
            |> Result.defaultWith (string >> failtest)

        do! register database.Fixture actor 3 service

        let! granted =
            grant
                actor
                (id database.Fixture 4)
                service
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        do!
            rewrite
                database
                (accepted granted).Position.Sequence
                (updatePeople
                    "people.grant"
                    (Submissions.grant
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        })
                    (fun person ->
                        { person with
                            Grants =
                                [
                                    {
                                        Grant =
                                            {
                                                Role = Role.Owner
                                                Scope = Scope.Installation
                                            }
                                        Active = true
                                    }
                                ]
                        }))


        do! finding VerificationFinding.HumanRequired database
    }

let internal lastOwner (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! unchanged =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 20))
                        (database.Fixture.Owner)
                        (None)
                        "people.enable"
                        (Map.empty)

                return! Service.executeSubmission (actor) submission ((token))
            }))

        do!
            rewrite
                database
                (accepted unchanged).Position.Sequence
                (updatePeople "people.disable" Map.empty (fun person ->
                    { person with Enabled = false }))

        do! finding VerificationFinding.StateSequence database
    }

let internal asOfCapabilityRequired (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let reader = target "reader"
        do! register database.Fixture actor 3 reader

        let! opened =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (10)
                    ("synthetic-a")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (token))

        do!
            rewrite
                database
                (accepted opened).Position.Sequence
                (VerificationScenarioInputs.executor (CaseFixtures.actorId (id database.Fixture 3)))

        do! finding VerificationFinding.StateSequence database
    }

let internal precedence (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        do!
            guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256),entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4"
                [||]

        do! finding VerificationFinding.Link database
    }

let internal priorCaseRequired (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! loaded =
            Service.get
                actor
                {
                    Target = caseTarget database.Fixture "synthetic-a"
                }
                token

        let snapshot =
            match loaded with
            | Outcome.Succeeded value -> value.Snapshot
            | _ -> failtest "The real later case must exist for the historical-order control"

        Expect.equal
            snapshot.CaseId
            (CaseFixtures.caseId (id database.Fixture 10))
            "Independent later creation identity"

        do!
            rewrite
                database
                (CaseFixtures.sequence 3L)
                (updatePeople
                    "people.grant"
                    (Submissions.grant
                        {
                            Role = Role.PaymentRecorder
                            Scope = GrantTarget.CaseId snapshot.CaseId
                        })
                    (fun person ->
                        { person with
                            Grants =
                                [
                                    {
                                        Grant =
                                            {
                                                Role = Role.PaymentRecorder
                                                Scope = Scope.Case snapshot.CaseId
                                            }
                                        Active = true
                                    }
                                ]
                        }))


        let caseId = CaseFixtures.caseId (id database.Fixture 10)

        do!
            sql
                database
                "UPDATE claimcore.grants SET case_id=$1 WHERE actor_id=$2 AND role=$3"
                [|
                    box (CaseId.value caseId)
                    box (id database.Fixture 1)
                    box (Role.token Role.PaymentRecorder)
                |]

        do! finding VerificationFinding.StateSequence database
    }
