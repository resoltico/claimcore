module ClaimCore.IntegrationTests.ReceiptContextTests

open ClaimCore.Contract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private commitSnapshot (_: ClaimCore.TestSupport.PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "registrar"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Core.execute
                admin
                (OperationInput.Authority(
                    authorityRequest (id 3) principal AuthorityCommandAct.Register
                ))
                (CancellationToken.None))

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) principal Role.CaseRegistrar GrantTarget.Installation true

        acceptedAuthority granted

        let afterWrite () =
            task {
                let amendment =
                    request
                        11
                        "PROBE-1"
                        1L
                        (UnvalidatedCaseAct.AmendRegistration
                            { registration with
                                ClaimantName = "Later Party"
                            })

                let! amended =
                    (Core.execute admin (OperationInput.Case amendment) (CancellationToken.None))

                accepted amended |> ignore

                let! revoked =
                    grant admin (id 5) principal Role.CaseRegistrar GrantTarget.Installation false

                acceptedAuthority revoked
            }

        let store = AfterCaseWriteStore(fixture.Store, afterWrite)

        let actor =
            Service.create fixture.InstallationId (store :> IStore) fixture.Calendar principal

        let! result =
            (Core.execute
                actor
                (OperationInput.Case(openRequest 10 "PROBE-1"))
                (CancellationToken.None))

        let receipt = accepted result
        Expect.equal store.ReadCalls 0 "No state is fetched after commit to shape the receipt"

        Expect.equal
            (PartyName.format (disclosedFields receipt).Registration.ClaimantName)
            registration.ClaimantName
            "Disclosure uses the write's locked context"

        Expect.equal receipt.RecordedBy (id 3) "Original executor"

        let! denied =
            (Core.get
                (core fixture principal)
                ({ CaseReference = "PROBE-1" })
                (CancellationToken.None))

        Expect.equal
            denied
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Later reads use revoked authority"
    }

[<Tests>]
let tests =
    testList
        "committed receipt context"
        [
            testCaseAsync
                "receipts retain locked disclosure when authority and revision change before delivery"
                (run commitSnapshot)
        ]
