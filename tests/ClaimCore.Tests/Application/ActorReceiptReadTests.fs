module ClaimCore.Tests.ActorReceiptReadTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport

let private id number =
    Guid.Parse(sprintf "e93eb71c-1904-4604-acb9-%012d" number)

[<Tests>]
let tests =
    testList
        "current actor receipt read projections"
        [
            testCase
                "missing or mismatched authorized work references are integrity errors"
                (fun () ->
                    let parent = CaseFixtures.caseId (id 90)
                    let identity = CaseFixtures.actorId (id 91)

                    let snapshot: CaseWorkSnapshot =
                        {
                            CaseId = parent
                            Revision = Revision.first
                            Work =
                                {
                                    Responsible = Some identity
                                    Summary = None
                                }
                        }

                    Expect.equal
                        (CaseWorkReadModels.snapshot Map.empty snapshot)
                        (Error CoreFault.StoreIntegrityError)
                        "An absent authorized account cannot become an invented erased or unknown name"

                    let other =
                        Inputs.namedActorReference (CaseFixtures.actorId (id 92)) "Wrong person"

                    Expect.equal
                        (CaseWorkReadModels.snapshot
                            (Map.ofList [ (parent, identity), other ])
                            snapshot)
                        (Error CoreFault.StoreIntegrityError)
                        "A map key cannot disguise a different actor identity")
        ]
