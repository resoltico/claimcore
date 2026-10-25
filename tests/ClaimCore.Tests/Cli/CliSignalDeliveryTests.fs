module ClaimCore.Tests.CliSignalDeliveryTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open CliTransportFixture

type private InterleavedErrors(betweenWrites: unit -> unit) =
    inherit MemoryStream()
    let mutable first = true

    override _.Write(bytes: ReadOnlySpan<byte>) =
        base.Write bytes

        if first then
            first <- false
            betweenWrites ()

let private interruptBetweenWrites
    signal
    (attempted: ManualResetEventSlim)
    (completed: ManualResetEventSlim)
    (code: int ref)
    =
    let thread =
        Thread(
            ThreadStart(fun () ->
                attempted.Set()
                code.Value <- signal ()
                completed.Set())
        )

    thread.Start()

    Expect.isTrue
        (attempted.Wait 5000)
        "The signal attempts to interrupt between frame and newline."

    Expect.isTrue
        (SpinWait.SpinUntil(
            (fun () ->
                completed.IsSet
                || (thread.ThreadState &&& ThreadState.WaitSleepJoin) <> ThreadState.Running),
            5000
        ))
        "The signal either blocks on delivery or completes before the newline."

    thread

let private completeRecords (errors: MemoryStream) original =
    let lines =
        Encoding.UTF8.GetString(errors.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)

    for line in lines do
        Expect.isTrue
            (line.StartsWith("Outcome unresolved.", StringComparison.Ordinal)
             || (Frames.admit (bytes line) |> Result.isOk)
             || (Frames.decode (bytes line) |> Result.isOk))
            "Every stderr record is complete even at the forced signal interleaving."

    Expect.equal
        (lines
         |> Array.last
         |> bytes
         |> Frames.admit
         |> Fixtures.accepted
         |> Frames.inputBytes)
        (Frames.inputBytes original)
        "The last line remains an exact retry frame."

let private interleaved failing =
    use attempted = new ManualResetEventSlim()
    use completed = new ManualResetEventSlim()
    use output = new MemoryStream()
    let mutable interrupt: Thread option = None
    let mutable signal = fun () -> 0
    let code = ref 0

    let betweenWrites () =
        interrupt <- Some(interruptBetweenWrites signal attempted completed code)

    use errors = new InterleavedErrors(betweenWrites)
    let delivery = FrameDelivery(output, errors, "en")
    signal <- delivery.Interrupt
    delivery.BeginFrame()
    let original = frame "operation.execute"
    delivery.BeforeDispatch original

    if failing then
        Expect.equal
            (delivery.Failure(IOException "synthetic-output-loss"))
            4
            "Output loss stays uncertain."
    else
        delivery.Write(
            CliReply.local ClientFailure.Unavailable
            |> CliReply.forAttempt original DeliveryState.Uncertain
        )

    Expect.isTrue
        ((interrupt |> Option.get).Join 5000)
        "The signal finishes after the stderr record."

    Expect.equal code.Value 4 "Interrupt retains mutation uncertainty."
    completeRecords errors original

[<Tests>]
let tests =
    testList
        "CLI signal delivery serialization"
        [
            testCase "signal cannot split a normal retry frame" (fun () -> interleaved false)
            testCase "signal cannot split output-loss failure records" (fun () -> interleaved true)
        ]
