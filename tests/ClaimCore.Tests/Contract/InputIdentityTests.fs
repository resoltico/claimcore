module ClaimCore.Tests.InputIdentityTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation

[<Tests>]
let tests =
    testCase "an unknown principal kind is rejected at its wire input" (fun () ->
        let raw =
            """{"kind":"UNDECLARED","issuer":"https://issuer.example/","subject":"synthetic-subject"}"""

        match
            Codec.decode
                Limits.MaximumRequestBytes
                RequestCodecs.principal
                (Encoding.UTF8.GetBytes raw)
        with
        | Error problem ->
            Expect.equal problem.Path "$.kind" "An unknown kind cannot become a subject refusal"
        | Ok _ -> failtest "Undeclared principal kinds cannot reach Domain."

        let role =
            """{"operationId":"10000000-0000-7000-8000-000000000001","task":"people.grant","binding":{"kind":"PEOPLE","actorId":"10000000-0000-7000-8000-000000000002","expectedRevision":1},"inputs":{"input:role":"UNDECLARED","input:scope":"INSTALLATION"}}"""

        match
            Codec.decode
                Limits.MaximumRequestBytes
                OperationSubmissionCodecs.submission
                (Encoding.UTF8.GetBytes role)
        with
        | Error problem ->
            Expect.equal
                problem.Path
                "$.inputs.input:role"
                "An undeclared role is a protocol fault at its authored token"
        | Ok _ -> failtest "Undeclared roles cannot reach Domain.")

let private malformedIdentity () =
    for amount in [ "01"; "-1"; "1e0"; "NaN"; "" ] do
        let input =
            Submissions.create
                "case.open"
                (System.Guid.Parse "20000000-0000-7000-8000-000000000001")
                (Binding.CaseNew Revision.zero)
                (Submissions.fields (RegistrationInput.values Inputs.registration)
                 |> Map.add
                     (Submissions.field BusinessField.CaseReference)
                     (ClaimCore.Application.Contracts.SubmissionValue.Text "IDENTITY-1")
                 |> Map.add
                     (Submissions.field BusinessField.ClaimedAmount)
                     (ClaimCore.Application.Contracts.SubmissionValue.Text amount))

        let wire = OperationSubmissionCodecs.submission.Write input

        let admitted =
            OperationSubmissionCodecs.submission.Read "$" wire
            |> ClaimCore.Tests.Fixtures.accepted

        Expect.equal
            admitted.Envelope.Inputs[Submissions.field BusinessField.ClaimedAmount]
            (ClaimCore.Application.Contracts.SubmissionValue.Text amount)
            "Wire admission preserves malformed authored scalars"

        let expected =
            if amount = "" then
                Violation.TextRequired
            else
                Violation.DecimalFormat(18, 4)

        Expect.equal
            (ClaimCore.Application.Contracts.OperationSubmission.syntax admitted
             |> Option.map Refusal.InvalidInput)
            (Some(Refusal.invalidInput (InputTarget.Field BusinessField.ClaimedAmount, expected)))
            "The original malformed spelling reaches its field-specific diagnostic"


[<Tests>]
let malformedAmounts =
    testCase "malformed amount spelling reaches its original field diagnostic" malformedIdentity
