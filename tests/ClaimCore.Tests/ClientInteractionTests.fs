module ClaimCore.Tests.ClientInteractionTests

open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open CliTransportFixture

let private decoded reply =
    Frames.decode reply.Bytes |> Fixtures.accepted

let private priorUncertainty () =
    let original = frame "operation.execute"
    let retry = Frames.retry original

    Expect.equal
        (Frames.inputBytes retry)
        (Frames.inputBytes original)
        "Retry context cannot alter request bytes."

    Expect.equal
        (Frames.encodeInvocation retry)
        (Frames.encodeInvocation original)
        "Retry context is outside the request frame."

    for reason in ClaimCore.Contract.Protocol.HostFailures.all do
        let reply = CliReply.serviceFailure retry (HostFailureCodecs.ofReason reason)
        Expect.equal reply.ExitCode 4 "A later host failure cannot disprove the original commit."

        Expect.equal
            (decoded reply).Delivery
            DeliveryState.Uncertain
            "Prior uncertainty survives every host phase."

    for outcome in
        [
            ExecuteResult.Rejected Refusal.ResourceUnavailable
            ExecuteResult.Rejected Refusal.IdempotencyConflict
            ExecuteResult.Failed CoreFault.StoreUnavailable
            ExecuteResult.Failed CoreFault.StoreIntegrityError
            ExecuteResult.OutcomeUnknown
        ] do
        let reply =
            Api.operationExecute.Output.Write
                {
                    OperationId = CliReplyFixtures.id
                    Outcome = outcome
                }

        Expect.equal
            (CliReply.result retry reply).ExitCode
            4
            "Rejection, store failure and lost confirmation cannot resolve an earlier unknown commit."

    for failure in
        [
            ClientFailure.Authentication
            ClientFailure.Configuration
            ClientFailure.Cancelled
            ClientFailure.Unavailable
        ] do
        let reply =
            CliReply.local failure |> CliReply.forAttempt retry DeliveryState.NotSent

        Expect.equal
            reply.ExitCode
            4
            "A retry that never dispatches leaves the earlier commit unknown."

let private acceptance () =
    let retry = frame "operation.execute" |> Frames.retry

    for outcome in
        [
            ExecuteResult.Accepted CliReplyFixtures.receipt
            ExecuteResult.AlreadyAccepted CliReplyFixtures.receipt
        ] do
        let value =
            Api.operationExecute.Output.Write
                {
                    OperationId = CliReplyFixtures.id
                    Outcome = outcome
                }

        let reply = CliReply.result retry value
        Expect.equal reply.ExitCode 0 "A correlated acceptance proves the operation was accepted."

        Expect.equal
            (decoded reply).Delivery
            DeliveryState.Delivered
            "Exact acceptance resolves prior uncertainty."

let private freshAttempt () =
    let fresh = frame "operation.execute"
    let before = HostFailureCodecs.ofReason HostFailureReason.BeforeDispatchFailed

    Expect.equal
        (CliReply.serviceFailure fresh before).ExitCode
        3
        "A fresh NOT_STARTED failure is definite for this attempt."

    let rejected =
        Api.operationExecute.Output.Write
            {
                OperationId = CliReplyFixtures.id
                Outcome = ExecuteResult.Rejected Refusal.ResourceUnavailable
            }

    Expect.equal
        (CliReply.result fresh rejected).ExitCode
        2
        "A fresh business rejection records nothing."

    Expect.throws
        (fun () -> frame "case.get" |> Frames.retry |> ignore)
        "Reads cannot carry uncertain-mutation retry context."

[<Tests>]
let tests =
    testList
        "frozen operation retry knowledge"
        [
            testCase "later failures preserve prior uncertainty" priorUncertainty
            testCase "matching acceptance resolves uncertainty" acceptance
            testCase "fresh attempts retain definite refusals" freshAttempt
        ]
