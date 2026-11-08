module ClaimCore.Tests.InputDiagnosticTests

open ClaimCore.Cli

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application

let private principal () =
    for kind in [ PrincipalKind.Human; PrincipalKind.Service ] do
        Expect.isOk
            (PrincipalKey.ofKind kind "https://issuer.example/" "synthetic")
            "Valid authored principal"

        for issuer, subject, target, issue in
            [
                "", "", InputTarget.PrincipalIssuer, Violation.TextRequired
                "https://issuer.example/", "", InputTarget.PrincipalSubject, Violation.TextRequired
                "secret-canary", "synthetic", InputTarget.PrincipalIssuer, Violation.PrincipalFormat
            ] do
            let expected =
                if issuer = "" && subject = "" then
                    [
                        {
                            Target = InputTarget.PrincipalIssuer
                            RelatedDate = None
                            Violation = Violation.TextRequired
                        }
                        {
                            Target = InputTarget.PrincipalSubject
                            RelatedDate = None
                            Violation = Violation.TextRequired
                        }
                    ]
                else
                    [
                        {
                            Target = target
                            Violation = issue
                            RelatedDate = None
                        }
                    ]
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
                |> Error

            Expect.equal
                (PrincipalKey.ofKind kind issuer subject)
                expected
                "Pure admission preserves cause without input"

let private localDiagnostics () =
    for setting, _ in InputDiagnostics.settings do
        for issue, _ in ConfigurationIssue.all do
            let frame: ClaimCore.Cli.FrameReply =
                {
                    Endpoint = None
                    OperationId = None
                    Delivery = DeliveryState.NotSent
                    Result = FrameResult.LocalFailure(CliFailure.ConfigurationIssue(setting, issue))
                }

            let bytes = ClaimCore.Cli.Frames.encode frame

            Expect.equal
                (ClaimCore.Cli.Frames.decode bytes |> Result.map CliReplies.frameFacts)
                (Ok(CliReplies.frameFacts frame))
                "Closed configuration facts survive framing"

            Expect.equal (ClaimCore.Cli.Frames.exitCode frame) 3 "Definite pre-dispatch failure"

[<Tests>]
let tests =
    testList
        "repairable input diagnostics"
        [
            testCase "principal admission retains deterministic closed issues" principal
            testCase "configuration diagnostics retain machine delivery semantics" localDiagnostics
        ]

[<Tests>]
let paymentCountDiagnostic =
    testCase "payment collection limit retains its typed count argument on the wire" (fun () ->
        let literal =
            System.Text.Encoding.UTF8.GetBytes
                """{"code":"PAYMENT_COUNT_RANGE","maximumPayments":50}"""

        let expected = Violation.PaymentCountRange 50

        Expect.equal
            (ClaimCore.Json.Codec.decode 1024 DiagnosticCodecs.violation literal)
            (Ok expected)
            "Independent public limit vector"

        Expect.sequenceEqual
            (DiagnosticCodecs.violation.Write expected
             |> ClaimCore.Json.CanonicalWriter.bytes)
            literal
            "Encoding retains the same declared argument")
