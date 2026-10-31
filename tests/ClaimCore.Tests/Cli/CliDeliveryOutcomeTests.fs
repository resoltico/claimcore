module ClaimCore.Tests.CliDeliveryOutcomeTests

open ClaimCore.Protocol

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
                        (admittedReply request (Api.operationResolve.Output.Write outcome))
                            .ExitCode
                        expected
                        "Resolution outcome controls exit")
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
