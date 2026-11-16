module ClaimCore.IntegrationTests.RootVerificationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs

let private corrupt expected statement =
    run (fun database ->
        task {
            let! _ = populated database
            do! guardedSql database statement [||]
            do! finding expected database
        })

let private rehashedCase (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let! closed =
            Service.executeSubmission
                actor
                (((request (database.Fixture) (11) ("synthetic-a") (1L) "case.close" (Map.empty))))
                token

        accepted closed |> ignore
        let! original = row database (CaseFixtures.sequence 4L)

        let submission, snapshot =
            livePayload (CaseFixtures.reference "synthetic-a") original

        let after =
            { snapshot with
                State =
                    CaseState.map
                        (fun fields ->
                            { fields with
                                Status = CaseStatus.Closed
                            })
                        snapshot.State
            }

        do! rewrite database original.Root.Header.Sequence (updateCase submission after)

        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=$1,status='CLOSED' WHERE case_id=$2"
                [|
                    box (
                        CaseRecord.encodeSnapshot
                            { after with
                                Revision = CaseFixtures.revision 2L
                            }
                    )
                    box (CaseId.value after.CaseId)
                |]

        do! finding VerificationFinding.StateSequence database
    }

let private actors (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "disabled-root-executor"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 20))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile))

                return! Service.executeSubmission (owner) submission (token)
            })

        acceptedPeople registered

        let! disabled =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 21))
                        (principal)
                        (None)
                        "people.disable"
                        (Map.empty)

                return! Service.executeSubmission (owner) submission (token)
            })

        acceptedPeople disabled

        let! opened =
            Service.executeSubmission
                owner
                (((request
                    (database.Fixture)
                    (22)
                    ("executor-root")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        let receipt = accepted opened
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun retained ->
                rehashed
                    { retained.Root.Header with
                        ActorId =
                            ActorId.ofOperationId (
                                CaseFixtures.operationId (id database.Fixture 20)
                            )
                    }
                    retained.Root.Structure.Task
                    retained.Root.Structure.Binding
                    retained.Root.Structure.Retained
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments)


        do! finding VerificationFinding.ActorNotEnabled database
    }

[<Tests>]
let tests =
    testList
        "current operation verification negatives"
        [
            testCaseAsync
                "link corruption precedes root interpretation"
                (corrupt
                    VerificationFinding.Link
                    "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,(get_byte(previous_hash,0)+1)%256) WHERE sequence=4")
            testCaseAsync
                "wrong stored root hash cannot enter replay"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger SET entry_hash=set_byte(entry_hash,0,(get_byte(entry_hash,0)+1)%256) WHERE sequence=4")
            testCaseAsync
                "changed salt cannot borrow an accepted compartment commitment"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger_compartments SET nonce=set_byte(nonce,0,(get_byte(nonce,0)+1)%256) WHERE operation_sequence=4")
            testCaseAsync
                "unknown public command members are not silently normalized away"
                (corrupt
                    VerificationFinding.PayloadFormat
                    "UPDATE claimcore.ledger SET structure=structure || '{\"unknownMember\":null}'::jsonb WHERE sequence=4")
            testCaseAsync
                "rehashed case result and matching projection still require independent replay"
                (run rehashedCase)
            testCaseAsync
                "a rehashed accepted command cannot borrow a disabled executor"
                (run actors)
        ]
