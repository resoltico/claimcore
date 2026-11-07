module ClaimCore.Tests.CliDeliveryOutcomeTests

open ClaimCore.Wire
open ClaimCore.Presentation

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Json
open CliTransportFixture

let private admittedReply invocation value =
    CliReply.result invocation value |> Fixtures.accepted

[<Tests>]
let tests =
    testList
        "one-shot operation delivery"
        [
            testCase "resolve has definitive exit codes" (fun () ->
                let request = frame "operation.resolve"

                for outcome, expected in
                    [
                        (Resolved.Accepted >> Outcome.Succeeded) CliReplyFixtures.receipt, 0
                        (Outcome.Succeeded Resolved.NotAccepted), 2
                        Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable), 2
                        Outcome.Failed CoreFault.StoreUnavailable, 3
                    ] do
                    Expect.equal
                        (admittedReply
                            request
                            (Api.operationResolve.Output.Write
                                {
                                    OperationReply.OperationId =
                                        Frames.operationId request |> Option.get
                                    Outcome = outcome
                                }))
                            .ExitCode
                        expected
                        "Resolution outcome controls exit")
            testCase "resolution text retains acceptance and passive unresolved status" (fun () ->
                let accepted =
                    TextOperationReply.resolveLines
                        "en"
                        (Outcome.Succeeded(Resolved.Accepted CliReplyFixtures.receipt))
                    |> String.concat "\n"

                Expect.stringContains
                    accepted
                    (CliReplyFixtures.id.ToString("D"))
                    "Existing acceptance has its receipt identity"

                Expect.isFalse
                    (accepted.Contains "permanently blocked")
                    "Acceptance is never presented as nonacceptance"

                Expect.equal
                    (TextOperationReply.resolveLines "en" (Outcome.Succeeded Resolved.NotAccepted))
                    [
                        "This operation ID was not accepted and is permanently blocked for this account."
                    ]
                    "Only actual final nonacceptance gets this completion"

                Expect.equal
                    (TextOperationReply.statusLines
                        "en"
                        (Outcome.Succeeded OperationStatus.Unresolved))
                    [ "The operation is still unresolved. This read has not blocked it." ]
                    "Passive checking is not settlement")
            testCase "reply shape failures are ordinary results" (fun () ->
                let invocation = frame "operation.execute"

                Expect.equal
                    (CliReply.result invocation JsonValue.Null |> Result.map _.Bytes)
                    (Error CliFailure.InvalidResponse)
                    "Untrusted validation failure must not throw or deliver a reply")
            testCase "fresh attempts retain definite refusals" (fun () ->
                let request = frame "operation.execute"

                Expect.equal
                    (CliReply.serviceFailure
                        request
                        (HostFailureCodecs.ofReason HostFailureReason.BeforeDispatchFailed))
                        .ExitCode
                    3
                    "No execution began"

                let rejected =
                    Api.operationExecute.Output.Write
                        {
                            OperationId = CliReplyFixtures.id
                            Outcome =
                                ExecuteResult.Rejected(
                                    Refusal.Access AccessRefusal.ResourceUnavailable
                                )
                        }

                Expect.equal (admittedReply request rejected).ExitCode 2 "Known business refusal")
            testCase "exact resend preserves frozen bytes" (fun () ->
                let original = frame "operation.execute"
                let resent = Frames.encodeInvocation original |> Frames.admit |> Fixtures.accepted

                Expect.equal
                    (Frames.inputBytes resent)
                    (Frames.inputBytes original)
                    "The frame carries the exact prepared request")
        ]
