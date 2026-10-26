module ClaimCore.Tests.CliDeliveryOutcomeTests

open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
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
                        ResolveReply.Accepted CliReplyFixtures.receipt, 0
                        ResolveReply.NotAccepted, 2
                        ResolveReply.Rejected Refusal.ResourceUnavailable, 2
                        ResolveReply.Failed CoreFault.StoreUnavailable, 3
                    ] do
                    Expect.equal
                        (admittedReply request (Api.operationResolve.Output.Write outcome))
                            .ExitCode
                        expected
                        "Resolution outcome controls exit")
            testCase "reply shape and identity failures are ordinary results" (fun () ->
                let invocation = frame "operation.execute"

                let contradictory =
                    Api.operationExecute.Output.Write
                        {
                            OperationId = System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                            Outcome = ExecuteResult.Rejected Refusal.ResourceUnavailable
                        }

                for value in [ JsonValue.Null; contradictory ] do
                    Expect.equal
                        (CliReply.result invocation value)
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
                            Outcome = ExecuteResult.Rejected Refusal.ResourceUnavailable
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
