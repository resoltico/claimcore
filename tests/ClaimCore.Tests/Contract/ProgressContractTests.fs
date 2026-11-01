module ClaimCore.Tests.ProgressContractTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase
        "wire case fields publish core progress and reject a contradictory progress value"
        (fun () ->
            let value: CaseFields =
                {
                    Work = CaseWork.empty
                    CaseReference = Inputs.reference "PROGRESS-CORE"
                    Status = CaseStatus.Opened
                    Registration = Inputs.admittedRegistration Inputs.registration
                    Progress = PaymentProgress.Undecided
                }

            let wire = ResponseCodecs.caseFields.Write value

            Expect.equal
                (JsonValue.tryMember "progress" wire)
                (Some(JsonValue.String "UNDECIDED"))
                "Independent progress token"

            Expect.isOk (ResponseCodecs.caseFields.Read "$" wire) "Complete positive control"

            let changed =
                match wire with
                | JsonValue.Object fields ->
                    JsonValue.Object(
                        fields
                        |> List.map (fun (key, value) ->
                            key, if key = "progress" then JsonValue.String "PAID" else value)
                    )
                | _ -> failtest "Case fields must be an object"

            Expect.isError
                (ResponseCodecs.caseFields.Read "$" changed)
                "A shape-valid contradictory paid token cannot restore undecided state")
