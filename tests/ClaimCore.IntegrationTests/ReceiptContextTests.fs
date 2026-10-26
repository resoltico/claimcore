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
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 3) principal AdmittedAuthorityAct.Register
                ),
                CancellationToken.None
            )

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
                        (CaseAct.AmendRegistration
                            { registration with
                                ClaimantName = "Later Party"
                            })

                let! amended = admin.Execute(OperationInput.Case amendment, CancellationToken.None)
                accepted amended |> ignore

                let! revoked =
                    grant admin (id 5) principal Role.CaseRegistrar GrantTarget.Installation false

                acceptedAuthority revoked
            }

        let store = AfterCaseWriteStore(fixture.Store, afterWrite)

        let actor =
            Core.create fixture.InstallationId (store :> ILedgerStore) fixture.Calendar principal

        let! result =
            actor.Execute(OperationInput.Case(openRequest 10 "PROBE-1"), CancellationToken.None)

        let receipt = accepted result
        Expect.equal store.ReadCalls 0 "No state is fetched after commit to shape the receipt"

        Expect.equal
            (disclosedFields receipt).Registration.ClaimantName
            registration.ClaimantName
            "Disclosure uses the write's locked context"

        Expect.equal receipt.RecordedBy (id 3) "Original executor"

        let! denied =
            (core fixture principal).Get({ CaseReference = "PROBE-1" }, CancellationToken.None)

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
