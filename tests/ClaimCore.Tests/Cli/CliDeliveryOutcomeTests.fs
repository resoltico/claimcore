module ClaimCore.Tests.CliDeliveryOutcomeTests

open ClaimCore.Wire
open ClaimCore.Presentation

open Expecto
open ClaimCore.TestSupport
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
            testCase "lookup has final and pending exit codes" (fun () ->
                let request = frame "operation.get"

                for outcome, expected in
                    [
                        Outcome.Succeeded(
                            OperationLookupResult.Accepted(CliReplyFixtures.receipt, true)
                        ),
                        0
                        (Outcome.Succeeded OperationLookupResult.NotAccepted), 2
                        (Outcome.Succeeded(
                            OperationLookupResult.Pending(
                                System.DateTimeOffset.Parse "2026-11-07T10:10:00Z"
                            )
                        )),
                        4
                        Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable), 2
                        Outcome.Failed CoreFault.StoreUnavailable, 3
                    ] do
                    Expect.equal
                        (admittedReply
                            request
                            (Api.operationGet.Output.Write
                                {
                                    OperationReply.OperationId =
                                        Frames.operationId request |> Option.get
                                    Outcome = outcome
                                }))
                            .ExitCode
                        expected
                        "Lookup outcome controls exit")
            testCase "lookup text retains acceptance and pending deadline" (fun () ->
                let accepted =
                    TextOperationReply.getLines
                        "en"
                        (Outcome.Succeeded(
                            OperationLookupResult.Accepted(CliReplyFixtures.receipt, true)
                        ))
                    |> String.concat "\n"

                Expect.stringContains
                    accepted
                    (CliReplyFixtures.id.ToString("D"))
                    "Existing acceptance has its receipt identity"

                Expect.isFalse
                    (accepted.Contains "permanently blocked")
                    "Acceptance is never presented as nonacceptance"

                Expect.equal
                    (TextOperationReply.getLines
                        "en"
                        (Outcome.Succeeded OperationLookupResult.NotAccepted))
                    [ "This change was not recorded for your account." ]
                    "Only actual final nonacceptance gets this completion"

                Expect.equal
                    (TextOperationReply.getLines
                        "en"
                        (Outcome.Succeeded(
                            OperationLookupResult.Pending(
                                System.DateTimeOffset.Parse "2026-11-07T10:10:00Z"
                            )
                        )))
                    [ "Waiting for a final answer until 2026-11-07T10:10:00.0000000+00:00." ]
                    "Pending exposes its deadline")
            testCase
                "a delivered pending lookup never emits a mutation retry or leaves interruption uncertain"
                (fun () ->
                    let request = frame "operation.get"
                    let operation = Frames.operationId request |> Option.get
                    let finalAt = OperationLifetime.finalAt (CaseFixtures.operationId operation)

                    let response =
                        admittedReply
                            request
                            (Api.operationGet.Output.Write
                                {
                                    OperationReply.OperationId = operation
                                    Outcome =
                                        Outcome.Succeeded(OperationLookupResult.Pending finalAt)
                                })

                    use output = new System.IO.MemoryStream()
                    use errors = new System.IO.MemoryStream()
                    let delivery = FrameDelivery(output, errors, "en")
                    delivery.BeforeDispatch request
                    delivery.Write response

                    Expect.equal
                        response.ExitCode
                        4
                        "Pending remains an explicit unresolved outcome"

                    Expect.equal
                        response.Frame.Delivery
                        DeliveryState.Delivered
                        "The lookup itself was delivered"

                    Expect.equal errors.Length 0L "There is no mutation retry frame for a read"

                    Expect.equal
                        (delivery.Interrupt())
                        (ExitCode.value ExitCode.Cancelled)
                        "A read cannot leave a possible write behind")
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
