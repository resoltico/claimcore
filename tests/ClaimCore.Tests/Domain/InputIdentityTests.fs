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
            """{"kind":"GRANT","role":"UNDECLARED","scope":{"kind":"INSTALLATION"}}"""

        match
            Codec.decode
                Limits.MaximumRequestBytes
                RequestCodecs.authorityAct
                (Encoding.UTF8.GetBytes role)
        with
        | Error problem ->
            Expect.equal problem.Path "$.role" "An undeclared role is a protocol fault"
        | Ok _ -> failtest "Undeclared roles cannot reach Domain.")

let private malformedIdentity () =
    for amount in [ "01"; "-1"; "1e0"; "NaN"; "" ] do
        let request =
            ClaimCore.Tests.Fixtures.request
                0L
                (AuthoredCaseAct.Open(
                    ClaimCore.TestSupport.Inputs.registrationWith
                        [ BusinessField.ClaimedAmount, (amount) ]
                        (ClaimCore.Tests.Fixtures.registration)
                ))

        let wire =
            ClaimCore.Wire.RequestCodecs.caseRequest.Write(Inputs.caseRequest request)

        let admitted =
            ClaimCore.Wire.RequestCodecs.caseRequest.Read "$" wire
            |> ClaimCore.Tests.Fixtures.accepted
            |> Inputs.referencedCase

        Expect.equal admitted request "Wire admission preserves malformed authored scalars"

        let expected =
            if amount = "" then
                Violation.TextRequired
            else
                Violation.DecimalFormat(18, 4)

        Expect.equal
            (Case.parse admitted |> Result.map ignore)
            (Error(Refusal.invalidInput (InputTarget.Field BusinessField.ClaimedAmount, expected)))
            "The original malformed spelling reaches its field-specific diagnostic"


[<Tests>]
let malformedAmounts =
    testCase "malformed amount spelling reaches its original field diagnostic" malformedIdentity
