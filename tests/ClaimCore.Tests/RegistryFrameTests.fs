module ClaimCore.Tests.RegistryFrameTests

open System
open System.Text
open Expecto
open ClaimCore.Contract

let private id = Guid.Parse "10000000-0000-4000-8000-000000000001"
let private other = Guid.Parse "10000000-0000-4000-8000-000000000002"

let private accepted value =
    value
    |> Result.defaultWith (fun problem -> failtest (sprintf "Unexpected frame refusal: %A" problem))

let private bytes (text: string) = Encoding.UTF8.GetBytes text

let private source =
    """{
  "endpoint": "operation.execute",
  "input": {
    "operationId": "10000000-0000-4000-8000-000000000001",
    "scope": "AUTHORITY",
    "target": { "kind": "HUMAN", "issuer": "https://issuer.example/", "subject": "synthetic" },
    "act": { "kind": "REGISTER_ACTOR" }
  }
}"""

let private frozen () =
    let original = bytes source
    let prepared = Frames.admit original |> accepted
    let first = Frames.inputBytes prepared
    Array.fill original 0 original.Length 0uy
    let callerCopy = Frames.inputBytes prepared
    Array.fill callerCopy 0 callerCopy.Length 0uy

    Expect.equal
        (Frames.inputBytes prepared)
        first
        "Neither caller-owned byte array can alter prepared content."

    let retry = Frames.encodeInvocation prepared

    Expect.isFalse
        ((Encoding.UTF8.GetString retry).Contains '\n')
        "The retry frame is one physical line."

    let replay = Frames.admit retry |> accepted

    Expect.equal
        (Frames.inputBytes replay)
        first
        "Retry sends exactly the bytes frozen for the first attempt."

    Expect.equal
        (Frames.operationId replay)
        (Some id)
        "Preparation and retry retain the caller's ID."

let private reply result delivery =
    {
        Endpoint = Some "operation.execute"
        OperationId = Some id
        Delivery = delivery
        Result = result
    }

let private frameRoundTrip frame =
    Expect.equal
        (Frames.encode frame |> Frames.decode)
        (Ok frame)
        "The minimal frame retains its facts."

let private results () =
    for failure in
        [
            CliFailure.ConfigurationInvalid
            CliFailure.AuthenticationFailed
            CliFailure.ServiceUnavailable
            CliFailure.Cancelled
            CliFailure.Internal
            CliFailure.InvalidFrame
            CliFailure.InvalidResponse
            CliFailure.ContractMismatch
        ] do
        frameRoundTrip (reply (FrameResult.LocalFailure failure) DeliveryState.NotSent)

    for setting, _ in InputDiagnostics.settings do
        for issue, _ in InputDiagnostics.configurationIssues do
            frameRoundTrip (
                reply
                    (FrameResult.LocalFailure(CliFailure.ConfigurationIssue(setting, issue)))
                    DeliveryState.NotSent
            )

    for phase in [ None; Some ExecutionPhase.NotStarted; Some ExecutionPhase.StartedUnconfirmed ] do
        frameRoundTrip (
            reply
                (FrameResult.HostFailure { Code = "INTERNAL"; Phase = phase })
                DeliveryState.Uncertain
        )

    let output =
        ReplyCodecs.execute.Write
            {
                ExecuteReply.OperationId = id
                Outcome = ExecuteResult.Rejected Refusal.LastOwner
            }

    frameRoundTrip (reply (FrameResult.Reply output) DeliveryState.Delivered)

let private exits () =
    let local failure delivery =
        reply (FrameResult.LocalFailure failure) delivery |> Frames.exitCode

    Expect.equal
        (local CliFailure.ServiceUnavailable DeliveryState.Uncertain)
        4
        "A dispatched mutation without confirmation stays uncertain."

    Expect.equal
        (local CliFailure.ServiceUnavailable DeliveryState.NotSent)
        3
        "Pre-dispatch failure is definite."

    Expect.equal
        (local CliFailure.Cancelled DeliveryState.NotSent)
        130
        "Pre-dispatch cancellation retains its exit."

    Expect.equal
        (local CliFailure.Internal DeliveryState.NotSent)
        70
        "Local programming failure retains its exit."

    Expect.equal
        (local CliFailure.InvalidFrame DeliveryState.NotSent)
        2
        "Protocol refusal retains its exit."

    let output =
        ReplyCodecs.execute.Write
            {
                ExecuteReply.OperationId = id
                Outcome = ExecuteResult.OutcomeUnknown
            }

    Expect.equal
        (reply (FrameResult.Reply output) DeliveryState.Uncertain |> Frames.exitCode)
        4
        "Lost commit confirmation exits four."

let private refusingFrames () =
    for input in
        [
            "{}"
            "{\"endpoint\":\"case.execute\",\"input\":{}}"
            "{\"endpoint\":\"context\",\"input\":{},\"extra\":0}"
            "{\"endpoint\":\"context\",\"input\":{},\"input\":{}}"
        ] do
        Expect.isError
            (Frames.admit (bytes input))
            "Unknown, extra and duplicate frame members refuse."

    let invalid = reply (FrameResult.Reply(JsonValue.Object [])) DeliveryState.Delivered

    Expect.throws
        (fun () -> Frames.encode invalid |> ignore)
        "A frame cannot carry an invalid endpoint reply."

let private operationEcho () =
    let prepared = Frames.admit (bytes source) |> accepted

    for actual, expected in [ id, true; other, false ] do
        let output =
            ReplyCodecs.execute.Write
                {
                    ExecuteReply.OperationId = actual
                    Outcome = ExecuteResult.Rejected Refusal.LastOwner
                }

        Expect.equal
            (Correlation.matches (Frames.endpoint prepared) (Frames.input prepared) output)
            expected
            "Only the frozen operation ID settles a mutation."

let private grantEcho () =
    let input =
        Protocol.RequestCodecs.grantsRequest.Write
            {
                ActorId = id
                AfterCursor = None
                Limit = 50
            }

    let endpoint = Api.tryFind "authority.grants" |> Option.get

    for actual, expected in [ id, true; other, false ] do
        let page: AuthorityGrantPage =
            {
                Actor =
                    {
                        ActorId = actual
                        Principal =
                            {
                                Kind = PrincipalKinds.Human
                                Issuer = "https://issuer.example/"
                                Subject = "synthetic"
                            }
                        Enabled = true
                    }
                Items = []
                NextCursor = None
            }

        let output = Api.authorityGrants.Output.Write(ReadReply.Succeeded page)

        Expect.equal
            (Correlation.matches endpoint input output)
            expected
            "A grants page echoes its target actor."

    let refused =
        Api.authorityGrants.Output.Write(ReadReply.Rejected Refusal.ResourceUnavailable)

    Expect.isTrue
        (Correlation.matches endpoint input refused)
        "A refusal carries no disclosed actor."

[<Tests>]
let tests =
    testList
        "registry CLI frames"
        [
            testCase "preparation freezes one-line exact retry bytes" frozen
            testCase "result frames carry only delivery and validated results" results
            testCase "uncertainty and local failures retain exit semantics" exits
            testCase "invalid frame boundaries reject" refusingFrames
            testCase "operation replies echo the frozen ID" operationEcho
            testCase "grants replies echo their actor" grantEcho
        ]
