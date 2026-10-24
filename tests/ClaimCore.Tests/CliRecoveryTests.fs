module ClaimCore.Tests.CliRecoveryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open ClaimCore.Tests.CliTransportFixture

let private recoveryId = Guid.Parse "90000000-0000-4000-8000-000000000100"

type private FlushFailure() =
    inherit MemoryStream()

    override _.Flush() =
        raise (IOException "Synthetic output failure")

let private outcome result request before _ =
    before ()

    Api.operationExecute.Output.Write
        {
            OperationId = CliReplyFixtures.id
            RecoveryId = Some recoveryId
            Outcome = result
        }
    |> CliReply.result request
    |> Task.FromResult

let private execute result retry brokenOutput brokenErrors failAcknowledgement =
    use input = new MemoryStream(frameBytes (frame Api.operationExecute.Id))

    use output: Stream =
        if brokenOutput then
            new FlushFailure()
        else
            new MemoryStream()

    use errors: Stream =
        if brokenErrors then
            new FlushFailure()
        else
            new MemoryStream()

    let mutable acknowledgements = 0

    let acknowledge request _ =
        acknowledgements <- acknowledgements + 1

        Expect.equal
            (Api.id (Frames.endpoint request))
            Api.operationForget.Id
            "Only operational acknowledgement"

        Expect.equal
            (Frames.input request)
            (Api.operationForget.Input.Write { RecoveryId = recoveryId })
            "Owned recovery echo"

        if failAcknowledgement then
            raise (IOException "Synthetic acknowledgement failure")

        Task.FromResult()

    use processor =
        RemoteFrameProcessor.ForTest(
            input,
            output,
            errors,
            outcome result,
            retry = retry,
            acknowledge = acknowledge
        )

    let code = processor.Call CancellationToken.None
    code, acknowledgements

let private rejected = ExecuteResult.Rejected Refusal.ResourceUnavailable

let private opaqueRetry result =
    let request =
        JsonValue.Object
            [
                "endpoint", JsonValue.String Api.operationRetry.Id
                "input",
                Api.operationRetry.Input.Write
                    {
                        RecoveryId = recoveryId
                        OperationId = CliReplyFixtures.id
                    }
            ]
        |> WireWriter.bytes
        |> Frames.admit
        |> Result.defaultWith (fun _ -> failtest "Recovery frame")

    let reply =
        Api.operationRetry.Output.Write
            {
                RecoveryId = Some recoveryId
                OperationId = CliReplyFixtures.id
                Outcome = result
            }
        |> CliReply.result request

    reply.ExitCode, RecoveryAcknowledgement.frame reply

[<Tests>]
let tests =
    testList
        "CLI recovery acknowledgement"
        [
            testCase
                "opaque retry rejection retains earlier uncertainty without an explicit retry flag"
                (fun () ->
                    let code, acknowledgement = opaqueRetry rejected
                    Expect.equal code 4 "Recovery selection is intrinsically uncertain"

                    Expect.isNone
                        acknowledgement
                        "A refusal cannot acknowledge the original unknown request")
            testCase "opaque retry acceptance permits acknowledgement" (fun () ->
                let code, acknowledgement =
                    opaqueRetry (ExecuteResult.AlreadyAccepted CliReplyFixtures.receipt)

                Expect.equal code 0 "Exact acceptance resolves uncertainty"
                Expect.isSome acknowledgement "Known accepted recovery can turn over")
            testCase "known terminal output acknowledges only after flush" (fun () ->
                Expect.equal
                    (execute rejected false false false false)
                    (2, 1)
                    "Known refusal acknowledged")
            testCase "failed acknowledgement cannot change a delivered result" (fun () ->
                Expect.equal (execute rejected false false false true) (2, 1) "No business retry")
            testCase "uncertain output retains recovery" (fun () ->
                Expect.equal
                    (execute ExecuteResult.OutcomeUnknown false false false false)
                    (4, 0)
                    "Uncertain result retained")
            testCase "earlier uncertainty survives a later known refusal" (fun () ->
                Expect.equal
                    (execute rejected true false false false)
                    (4, 0)
                    "No acknowledgement of prior uncertainty")
            testCase "output flush loss retains recovery even when both sinks fail" (fun () ->
                Expect.equal
                    (execute rejected false true true false)
                    (4, 0)
                    "OS output failure prevents acknowledgement")
        ]
