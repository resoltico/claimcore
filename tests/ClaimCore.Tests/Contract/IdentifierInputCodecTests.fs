module ClaimCore.Tests.IdentifierInputCodecTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport

let private operation = Guid.Parse "00000000-0000-7000-8000-000000000001"

let private required result =
    result |> Result.defaultWith (fun issue -> failtestf "%A" issue)

let private request task json =
    Encoding.UTF8.GetBytes(
        "{\"operationId\":\""
        + operation.ToString("D")
        + "\",\"task\":\""
        + task
        + "\",\"binding\":{\"kind\":\"CASE\",\"target\":{\"kind\":\"REFERENCE\",\"caseReference\":\"IDENTIFIERS\"},\"expectedRevision\":1},\"inputs\":"
        + json
        + "}"
    )

let private valid task json =
    Codec.decode 4096 OperationSubmissionCodecs.submission (request task json)
    |> required

let private rejected task json =
    match Codec.decode 4096 OperationSubmissionCodecs.submission (request task json) with
    | Error _ -> true
    | Ok input -> OperationSubmission.syntax input |> Option.isSome

[<Tests>]
let tests =
    testCase
        "declared input maps preserve optional identifiers and reject half-asserted or nested money"
        (fun () ->
            let facts =
                Inputs.registrationWith
                    [
                        BusinessField.PolicyNumber, "LV / Polise 42"
                        BusinessField.InsurerClaimReference, "ملف / 17"
                    ]
                    Inputs.registration

            let input =
                Submissions.create
                    "case.open"
                    operation
                    (Binding.CaseNew Revision.zero)
                    (Submissions.fields (RegistrationInput.values facts)
                     |> Map.add
                         (Submissions.field BusinessField.CaseReference)
                         (SubmissionValue.Text "IDENTIFIERS"))

            let encoded = OperationSubmissionCodecs.submission.Write input

            Expect.equal
                (JsonValue.tryMember "inputs" encoded
                 |> Option.bind (JsonValue.tryMember "field:policyNumber"))
                (Some(JsonValue.String "LV / Polise 42"))
                "Optional identifier reaches the typed create input"

            let decoded =
                valid
                    "case.amendRegistration"
                    "{\"field:policyNumber\":null,\"field:claimedAmount\":\"12.50\",\"field:claimedCurrency\":\"EUR\"}"

            let values = (Submissions.command decoded).Inputs.Values

            Expect.equal
                values[Submissions.field BusinessField.ClaimedAmount]
                (InputValue.Text "12.50")
                "Authored spelling remains exact"

            Expect.equal
                values[Submissions.field BusinessField.PolicyNumber]
                InputValue.Null
                "Explicit clear remains distinct"

            Expect.isNone (OperationSubmission.syntax decoded) "Complete flat money is valid"

            for json in
                [
                    "{\"field:claimedAmount\":\"12.50\"}"
                    "{\"field:claimedCurrency\":\"EUR\"}"
                    "{\"claimed\":{\"amount\":\"12.50\",\"currency\":\"EUR\"}}"
                ] do
                Expect.isTrue
                    (rejected "case.amendRegistration" json)
                    "Shape admission or the task-owned syntax check rejects incomplete/nested money"

            let decision json =
                "{\"group:registration\":\"KEEP\",\"group:decision\":\"UPDATE\"," + json + "}"

            let complete =
                valid
                    "case.correct"
                    (decision "\"field:payableAmount\":\"12.50\",\"field:payableCurrency\":\"EUR\"")

            Expect.isNone
                (OperationSubmission.syntax complete)
                "Decision update has the same atomic pair"

            Expect.isTrue
                (rejected "case.correct" (decision "\"field:payableAmount\":\"12.50\""))
                "Decision money cannot be half asserted")
