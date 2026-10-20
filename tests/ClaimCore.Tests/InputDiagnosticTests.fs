module ClaimCore.Tests.InputDiagnosticTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Catalog
open ClaimCore.Wire
open ClaimCore.Application

let private principal () =
    for kind in [ PrincipalKinds.Human; PrincipalKinds.Service ] do
        Expect.isOk
            (PrincipalKey.admit kind "https://issuer.example/" "synthetic")
            "Valid authored principal"

        for issuer, subject, target, issue in
            [
                "", "", PrincipalInputTarget.TargetIssuer, InputIssue.Missing
                "https://issuer.example/",
                "",
                PrincipalInputTarget.TargetSubject,
                InputIssue.Missing
                "secret-canary", "synthetic", PrincipalInputTarget.TargetIssuer, InputIssue.Invalid
            ] do
            Expect.equal
                (PrincipalKey.admit kind issuer subject)
                (Error { Target = target; Issue = issue })
                "Pure admission preserves cause without input"

let private hostDiagnostics () =
    for target, _ in InputDiagnostics.targets do
        for issue, _ in InputDiagnostics.issues do
            let reason = HostFailure.PrincipalInput { Target = target; Issue = issue }
            let bytes = HostFailures.encode reason

            Expect.equal
                (HostFailures.decode bytes |> Result.map _.Reason)
                (Ok reason)
                "Same codec round trip"

            let original = JsonNode.Parse bytes |> nonNull

            for key, value in
                [
                    "status", JsonValue.Create(401) :> JsonNode
                    "code", (JsonValue.Create("SESSION_REJECTED") |> nonNull) :> JsonNode
                    "executionPhase",
                    (JsonValue.Create("STARTED_UNCONFIRMED") |> nonNull) :> JsonNode
                ] do
                let poison = original.DeepClone()
                poison[key] <- value.DeepClone()

                Expect.isError
                    (HostFailures.decode (Encoding.UTF8.GetBytes(poison.ToJsonString())))
                    "Principal detail cannot cross status/code/phase boundary"

let private localDiagnostics () =
    let metadata =
        {
            OperationId = None
            AttemptKnowledge = AttemptKnowledge.NotSent
            ResolutionEvidence = ResolutionEvidence.NoEvidence
        }

    for setting, _ in InputDiagnostics.settings do
        for issue, _ in InputDiagnostics.configurationIssues do
            let frame =
                FrameReply.LocalFailure(
                    metadata,
                    None,
                    LocalFailure.ConfigurationIssue(setting, issue)
                )

            let bytes = Frames.encode frame

            Expect.equal
                (Frames.decode bytes)
                (Ok frame)
                "Closed configuration facts survive framing"

            Expect.equal (Frames.exitCode frame) 3 "Definite pre-dispatch failure"

[<Tests>]
let tests =
    testList
        "repairable input diagnostics"
        [
            testCase "principal admission retains deterministic closed issues" principal
            testCase "principal diagnostics use the existing strict host codec" hostDiagnostics
            testCase "configuration diagnostics retain machine delivery semantics" localDiagnostics
        ]
