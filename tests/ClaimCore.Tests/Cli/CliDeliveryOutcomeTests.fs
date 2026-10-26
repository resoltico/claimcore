module ClaimCore.Tests.CliDeliveryOutcomeTests

open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Cli
open CliTransportFixture

[<Tests>]
let tests =
    testList
        "one-shot operation delivery"
        [
            testCase "resolve has definitive exit codes" (fun () ->
                let request = frame "operation.resolve"

                for outcome, expected in
                    [
                        ResolveReply.Accepted CliReplyFixtures.receipt, 0
                        ResolveReply.NotAccepted, 2
                        ResolveReply.Rejected Refusal.ResourceUnavailable, 2
                        ResolveReply.Failed CoreFault.StoreUnavailable, 3
                    ] do
                    Expect.equal
                        (CliReply.result request (Api.operationResolve.Output.Write outcome))
                            .ExitCode
                        expected
                        "Resolution outcome controls exit")
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
                            Outcome = ExecuteResult.Rejected Refusal.ResourceUnavailable
                        }

                Expect.equal (CliReply.result request rejected).ExitCode 2 "Known business refusal")
            testCase "exact resend preserves frozen bytes" (fun () ->
                let original = frame "operation.execute"
                let resent = Frames.encodeInvocation original |> Frames.admit |> Fixtures.accepted

                Expect.equal
                    (Frames.inputBytes resent)
                    (Frames.inputBytes original)
                    "The frame carries the exact prepared request")
        ]
