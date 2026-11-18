module ClaimCore.IntegrationTests.ReceiptContextTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private commitSnapshot (_: ClaimCore.TestSupport.PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "registrar"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 3))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! granted =
            grant
                admin
                (id fixture 4)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let afterWrite () =
            task {
                let amendment =
                    request
                        (fixture)
                        (11)
                        ("PROBE-1")
                        (1L)
                        "case.amendRegistration"
                        (ClaimCore.TestSupport.Submissions.updates (
                            RegistrationUpdateInput.values (
                                (DomainInputs.registrationUpdate (
                                    ClaimCore.TestSupport.DomainInputs.registrationWith
                                        [ BusinessField.ClaimantName, ("Later Party") ]
                                        (registration)
                                ))
                            )
                        ))

                let! amended =
                    (Service.executeSubmission admin ((amendment)) (CancellationToken.None))

                accepted amended |> ignore

                let! revoked =
                    grant
                        admin
                        (id fixture 5)
                        principal
                        {
                            Role = Role.CaseRegistrar
                            Scope = GrantTarget.Installation
                        }
                        false

                acceptedPeople revoked
            }

        let store = AfterCaseWriteStore(fixture.Store, afterWrite)

        let actor =
            Service.create
                BuildIdentity.current.Version
                fixture.InstallationId
                (store :> IStore)
                fixture.Calendar
                principal

        let! result =
            (Service.executeSubmission
                actor
                (((openRequest fixture 10 "PROBE-1")))
                (CancellationToken.None))

        let receipt = accepted result
        Expect.equal store.ReadCalls 0 "No state is fetched after commit to shape the receipt"

        Expect.equal
            (PartyName.format (
                Registration.claimant ((disclosedFields receipt).Registration) |> Option.get
            ))
            (RegistrationInput.tryValue BusinessField.ClaimantName (registration)
             |> Option.get)
            "Disclosure uses the write's locked context"

        Expect.equal
            (receipt.RecordedBy |> AuthorityRef.actor |> Option.get).ActorId
            ((CaseFixtures.actorId (id fixture 3)))
            "Original executor"

        let! denied =
            (Service.get
                (core fixture principal)
                ({
                    Target = CaseSelector.Reference("PROBE-1")
                })
                (CancellationToken.None))

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Later reads use revoked people"
    }

[<Tests>]
let tests =
    testList
        "committed receipt context"
        [
            testCaseAsync
                "receipts retain locked disclosure when people and revision change before delivery"
                (run commitSnapshot)
        ]
