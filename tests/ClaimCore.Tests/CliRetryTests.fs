module ClaimCore.Tests.CliRetryTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open CliTransportFixture

let private runFrame retry input invoke =
    use source = new MemoryStream(input: byte array)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    use processor =
        RemoteFrameProcessor.ForTest(source, output, errors, invoke, retry = retry)

    let code = processor.Call CancellationToken.None
    code, output.ToArray(), errors.ToArray()

let private retryLine errors =
    Encoding.UTF8.GetString(errors: byte array).Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.last
    |> bytes

let private savedRetry () =
    let original = frame "operation.execute"

    let lost request dispatched _ =
        dispatched ()

        Api.operationExecute.Output.Write
            {
                OperationId = CliReplyFixtures.id
                Outcome = ExecuteResult.OutcomeUnknown
            }
        |> CliReply.result request
        |> Task.FromResult

    let code, _, errors = runFrame false (frameBytes original) lost
    Expect.equal code 4 "Lost confirmation prints an exact retry frame."
    let saved = retryLine errors
    let admitted = Frames.admit saved |> Fixtures.accepted

    Expect.equal
        (Frames.inputBytes admitted)
        (Frames.inputBytes original)
        "Exported retry bytes are frozen."

    saved

let private lostThenRefused () =
    let saved = savedRetry ()

    let refused request dispatched _ =
        dispatched ()

        HostFailureCodecs.ofReason HostFailureReason.BeforeDispatchFailed
        |> CliReply.serviceFailure request
        |> Task.FromResult

    let code, output, errors = runFrame true saved refused
    Expect.equal code 4 "A later NOT_STARTED keeps the original outcome unknown."

    Expect.equal
        (Frames.decode output |> Fixtures.accepted).Delivery
        DeliveryState.Uncertain
        "The delivered reply cannot remove historical uncertainty."

    Expect.equal (retryLine errors) saved "Repeated uncertainty exports the identical frame."

    let replay request dispatched _ =
        dispatched ()

        Api.operationExecute.Output.Write
            {
                OperationId = CliReplyFixtures.id
                Outcome = ExecuteResult.AlreadyAccepted CliReplyFixtures.receipt
            }
        |> CliReply.result request
        |> Task.FromResult

    let code, output, errors = runFrame true saved replay
    Expect.equal code 0 "A matching acceptance resolves the retry."

    Expect.equal
        (Frames.decode output |> Fixtures.accepted).Delivery
        DeliveryState.Delivered
        "Acceptance is definite."

    Expect.isEmpty errors "A resolved operation needs no retry frame."

let private beforeDispatch () =
    let original = frame "operation.execute"

    let invoke _ _ _ =
        Task.FromResult(CliReply.local ClientFailure.Authentication)

    let code, _, errors = runFrame true (frameBytes original) invoke
    Expect.equal code 4 "A retry's authentication failure cannot erase earlier uncertainty."

    Expect.equal
        (retryLine errors)
        (frameBytes original)
        "Even a retry that cannot dispatch remains recoverable."

    let code, _, _ =
        runFrame true (frameBytes (frame "case.get")) (fun _ _ _ ->
            failtest "Read retry must not dispatch.")

    Expect.equal code 2 "Retry context is only valid for mutation frames."

    let code, _, _ =
        runFrame true [||] (fun _ _ _ -> failtest "Empty retry must not dispatch.")

    Expect.equal code 2 "An empty retry is an invalid frame, never a successful operation."

[<Tests>]
let tests =
    testList
        "CLI uncertain operation retries"
        [
            testCase
                "lost confirmation survives later refusal and resolves on acceptance"
                lostThenRefused
            testCase "pre-dispatch retry failures preserve the frozen operation" beforeDispatch
        ]
