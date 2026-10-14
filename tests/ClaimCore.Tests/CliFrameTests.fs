module ClaimCore.Tests.CliFrameTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
open CliTransportFixture

type private BrokenOutput() =
    inherit MemoryStream()

    override _.Flush() =
        raise (IOException "synthetic-output-failure")

let private deliveryKnowledge () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    Expect.equal (delivery.Interrupt()) 130 "Before dispatch cancellation"
    let mutation = FrameDelivery(output, errors)
    mutation.BeginFrame()
    mutation.BeforeDispatch(frame "case.steward")
    Expect.equal (mutation.Interrupt()) 4 "Cancellation after mutation dispatch"
    let completed = FrameDelivery(output, errors)
    completed.BeginFrame()
    completed.BeforeDispatch(frame "case.steward")
    completed.Write(CliReply.protocol "INVALID_RESPONSE")
    completed.BeginFrame()

    Expect.equal
        (completed.Interrupt())
        130
        "Next frame does not inherit flushed mutation knowledge"

    use broken = new BrokenOutput()
    let lost = FrameDelivery(broken, errors)
    lost.BeginFrame()
    lost.BeforeDispatch(frame "authority.setEnabled")

    try
        lost.Write(CliReply.local ClientFailure.Unavailable)
        failtest "Expected broken output"
    with :? IOException as error ->
        Expect.equal (lost.Failure error) 4 "Stdout loss retains uncertainty"

let private readerBounds () =
    for length, expected in [ FrameReader.MaximumBytes, false; FrameReader.MaximumBytes + 1, true ] do
        use input = new MemoryStream(Array.create length (byte 'x'))

        Expect.equal
            (FrameReader.readDocument input = InputFrame.TooLarge)
            expected
            "Bounded document"

        input.Position <- 0L
        Expect.equal (FrameReader.readLine input = InputFrame.TooLarge) expected "Bounded line"

    for text in [ ""; "\n"; "{}\r\n"; "{}" ] do
        use input = new MemoryStream(bytes text)

        match FrameReader.readLine input with
        | InputFrame.Bytes value ->
            Expect.isTrue (value.Length <= text.Length) "Bounded line preserves JSON whitespace"
        | InputFrame.EndOfInput -> Expect.equal text "" "Only EOF is empty input"
        | InputFrame.TooLarge -> failtest "Short input cannot overflow"

    for hostile in
        [
            bytes ""
            bytes "{}"
            [| 0xffuy |]
            bytes "{\"endpoint\":\"session\",\"input\":{},\"extra\":true}"
        ] do
        let mutable called = false

        let code, output, _ =
            call hostile (fun _ _ _ ->
                called <- true
                Task.FromResult(CliReply.local ClientFailure.Unavailable))

        Expect.equal code 2 "Invalid frame exits two"
        Expect.isFalse called "Admission precedes authentication and transport"
        Expect.isOk (Frames.decode output) "One protocol failure reply"

let private sessionLines () =
    let document = Frames.bytes (frame "case.get") |> Encoding.UTF8.GetString
    use input = new MemoryStream(bytes (document + "\r\n{}\n" + document + "\n"))
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let mutable invoked = 0

    use processor =
        RemoteFrameProcessor.ForTest(
            input,
            output,
            errors,
            fun _ _ _ ->
                invoked <- invoked + 1
                Task.FromResult(CliReply.local ClientFailure.Unavailable)
        )

    Expect.equal
        (processor.Session CancellationToken.None)
        3
        "Session exits with last delivered reply"

    Expect.equal invoked 2 "Bad middle frame is rejected independently"

    let lines =
        Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal lines.Length 3 "One reply for each line"

    for line in lines do
        Expect.isOk (Frames.decode (bytes line)) "Every response is a valid frame"

let private uncertainSession () =
    let first = Frames.bytes (frame "authority.register") |> Encoding.UTF8.GetString
    let next = Frames.bytes (frame "case.get") |> Encoding.UTF8.GetString
    use input = new MemoryStream(bytes (first + "\n" + next + "\n"))
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let mutable invoked = 0

    use processor =
        RemoteFrameProcessor.ForTest(
            input,
            output,
            errors,
            fun invocation dispatched _ ->
                invoked <- invoked + 1
                dispatched ()
                let reply = CliReply.local ClientFailure.Unavailable

                Task.FromResult(
                    if (Frames.endpoint invocation).Mutates then
                        CliReply.unknown reply
                    else
                        reply
                )
        )

    Expect.equal
        (processor.Session CancellationToken.None)
        4
        "A later frame cannot hide mutation uncertainty"

    Expect.equal invoked 1 "Uncertainty ends the session at the replayable frame"

let private offlineDiscovery () =
    for args in
        [
            [ "summary" ]
            [ "fields" ]
            [ "commands" ]
            [ "endpoints" ]
            [ "diagnostics" ]
        ] do
        Expect.isSome (Discovery.describe args) "Offline catalogue read"

    for args in
        [
            [ "invocation" ]
            [ "response" ]
            [ "authority.register"; "request" ]
            [ "host-failure" ]
        ] do
        Expect.isSome (Discovery.schema args) "Offline schemas"

    Expect.isNone (Discovery.describe [ "unsupported.synthetic" ]) "Unsupported selector refused"
    Expect.isNone (Discovery.schema [ "unsupported.synthetic" ]) "Unknown schema refused"

[<Tests>]
let tests =
    testList
        "CLI frame delivery"
        [
            testCase
                "cancellation and stdout loss preserve current frame knowledge"
                deliveryKnowledge
            testCase "bounded documents and NDJSON reject invalid frames before I/O" readerBounds
            testCase
                "uncertainty ends a session before another frame can overwrite exit four"
                uncertainSession
            testCase "sessions emit one validated reply per line" sessionLines
            testCase "discovery reads the shared catalogue without credentials" offlineDiscovery
        ]
