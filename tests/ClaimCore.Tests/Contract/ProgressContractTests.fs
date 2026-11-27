module ClaimCore.Tests.ProgressContractTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "wire case fields use financial facts and reject an extra progress claim" (fun () ->
        let value: CaseFields =
            {
                CaseReference = DomainInputs.reference "CC-XJR3-0H0C-6E7G-MEY0-04"
                Handling = Handling.Open
                Registration = DomainInputs.admittedRegistration DomainInputs.registration
                Finance = FinancialPosition.undecided
            }

        let wire = ResponseCodecs.caseFields.Write value

        Expect.isNone
            (JsonValue.tryMember "progress" wire)
            "Progress derives from admitted financial facts"

        Expect.isOk (ResponseCodecs.caseFields.Read "$" wire) "Complete positive control"

        let changed =
            match wire with
            | JsonValue.Object fields ->
                JsonValue.Object(fields @ [ "progress", JsonValue.String "PAID" ])
            | _ -> failtest "Case fields must be an object"

        Expect.isError
            (ResponseCodecs.caseFields.Read "$" changed)
            "The closed shape has no competing progress owner")
