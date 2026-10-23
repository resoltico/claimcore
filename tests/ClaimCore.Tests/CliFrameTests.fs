module ClaimCore.Tests.CliFrameTests

open ClaimCore.Contract

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open CliTransportFixture

type private BrokenOutput() =
    inherit MemoryStream()

    override _.Flush() =
        raise (IOException "synthetic-output-failure")

type private PartialOutput() =
    inherit MemoryStream()

    override _.Write(value: ReadOnlySpan<byte>) =
        base.Write(value.Slice(0, min value.Length 5))
        raise (IOException "synthetic-partial-write")

let private deliveryKnowledge () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = ClaimCore.Cli.FrameDelivery(output, errors)
    delivery.BeginFrame()
    Expect.equal (delivery.Interrupt()) 130 "Before dispatch cancellation"
    let mutation = ClaimCore.Cli.FrameDelivery(output, errors)
    mutation.BeginFrame()
    mutation.BeforeDispatch(frame "operation.execute")
    Expect.equal (mutation.Interrupt()) 4 "Cancellation after mutation dispatch"
    let completed = ClaimCore.Cli.FrameDelivery(output, errors)
    completed.BeginFrame()
    completed.BeforeDispatch(frame "operation.execute")
    completed.Write(CliReply.protocol CliFailure.InvalidResponse)
    completed.BeginFrame()

    Expect.equal
        (completed.Interrupt())
        130
        "Next frame does not inherit flushed mutation knowledge"

    use broken = new BrokenOutput()
    let lost = ClaimCore.Cli.FrameDelivery(broken, errors)
    lost.BeginFrame()
    lost.BeforeDispatch(frame "operation.execute")

    try
        lost.Write(CliReply.local ClientFailure.Unavailable)
        failtest "Expected broken output"
    with :? IOException as error ->
        Expect.equal (lost.Failure error) 4 "Stdout loss retains uncertainty"

let private readOutputFailure () =
    for dispatched, expected in [ false, "NOT_SENT"; true, "UNCERTAIN" ] do
        use output = new BrokenOutput()
        use errors = new MemoryStream()
        let delivery = ClaimCore.Cli.FrameDelivery(output, errors)
        let invocation = frame "case.get"
        delivery.BeginFrame()
        delivery.Prepare invocation

        if dispatched then
            delivery.BeforeDispatch invocation

        try
            delivery.Write(CliReply.local ClientFailure.Unavailable)
            failtest "Expected output flush failure"
        with :? IOException as error ->
            Expect.equal
                (delivery.Failure error)
                70
                "A failed read output remains a definite local failure"

        let failure = replyJson (errors.ToArray())

        Expect.equal
            (failure.GetProperty("delivery").GetString())
            expected
            "Read output failure retains actual dispatch facts"

        Expect.equal
            (failure.GetProperty("endpoint").GetString())
            "case.get"
            "Safe failure retains invocation endpoint"

let private partialOutputKnowledge () =
    for endpoint, code, knowledge in
        [ "operation.execute", 4, "UNCERTAIN"; "case.get", 70, "UNCERTAIN" ] do
        use output = new PartialOutput()
        use errors = new MemoryStream()
        let delivery = ClaimCore.Cli.FrameDelivery(output, errors)
        delivery.BeginFrame()
        delivery.BeforeDispatch(frame endpoint)

        try
            delivery.Write(CliReply.local ClientFailure.Unavailable)
            failtest "Expected partial stdout write failure"
        with :? IOException as error ->
            Expect.equal
                (delivery.Failure error)
                code
                "Partial output preserves delivery classification"

        Expect.equal output.Length 5L "Only a partial frame was written"
        let failure = replyJson (errors.ToArray())

        Expect.equal
            (failure.GetProperty("delivery").GetString())
            knowledge
            "Stderr safely reports the attempt knowledge"

        use brokenErrors = new PartialOutput()
        let both = ClaimCore.Cli.FrameDelivery(output, brokenErrors)
        both.BeginFrame()
        both.BeforeDispatch(frame endpoint)

        Expect.equal
            (both.Failure(IOException "synthetic-both-streams-failed"))
            code
            "The exit remains meaningful when stderr also fails"

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
    let document = frameBytes (frame "case.get") |> Encoding.UTF8.GetString
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
    let first = frameBytes (frame "operation.execute") |> Encoding.UTF8.GetString
    let next = frameBytes (frame "case.get") |> Encoding.UTF8.GetString
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
                    if (frameEndpoint invocation |> Api.mutates) then
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

let private describe arguments =
    match ClaimCore.Contract.Protocol.CliInvocations.admit ("describe" :: arguments) with
    | Ok(ClaimCore.Contract.Protocol.CliInvocation.Describe(selector, _, _)) ->
        Discovery.describe selector
    | _ -> None

let private schema arguments =
    match ClaimCore.Contract.Protocol.CliInvocations.admit ("schema" :: arguments) with
    | Ok(ClaimCore.Contract.Protocol.CliInvocation.Schema selection) -> Discovery.schema selection
    | _ -> None

let private offlineDiscovery () =
    for args in
        [
            [ "summary" ]
            [ "fields" ]
            [ "commands" ]
            [ "authority" ]
            [ "authority"; "REGISTER_ACTOR" ]
            [ "endpoints" ]
            [ "diagnostics" ]
        ] do
        Expect.isSome (describe args) "Offline catalogue read"

    for args in
        [
            [ "invocation" ]
            [ "response" ]
            [ "operation.execute"; "request" ]
            [ "host-failure" ]
        ] do
        Expect.isSome (schema args) "Offline schemas"

    Expect.isNone (describe [ "unsupported.synthetic" ]) "Unsupported selector refused"
    Expect.isNone (schema [ "unsupported.synthetic" ]) "Unknown schema refused"

let private exampleFrames () =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()

    let files =
        Directory.GetFiles(Path.Combine(root, "examples"), "*.json") |> Array.sort

    Expect.equal files.Length 9 "Every published example frame is reached"
    let mutable current = None

    for index, path in Array.indexed files do
        let invocation = Frames.admit (File.ReadAllBytes path) |> Fixtures.accepted

        Expect.equal
            (frameEndpoint invocation |> Api.id)
            "operation.execute"
            "Business and stewardship share execution"

        let request =
            match
                Api.operationExecute.Input.Read "$" (Frames.input invocation)
                |> Fixtures.accepted
            with
            | OperationInput.Case request -> request
            | _ -> failtest "Example must be a case request"

        let next =
            ClaimCore.Domain.Case.decide (DateOnly(2026, 10, 15)) request current
            |> Fixtures.accepted

        Expect.equal
            (ClaimCore.Domain.Case.snapshot next).Revision
            (int64 (index + 1))
            "Example revision sequence"

        current <- Some next

    let final = current |> Option.get |> ClaimCore.Domain.Case.snapshot
    Expect.equal final.Reference "DEMO-0001" "Original reference retained"
    Expect.equal final.View ClaimCore.Contract.CaseView.Erased "Examples end with explicit erasure"

[<Tests>]
let tests =
    testList
        "CLI frame delivery"
        [
            testCase
                "all published example frames admit and follow the Domain lifecycle"
                exampleFrames
            testCase
                "cancellation and stdout loss preserve current frame knowledge"
                deliveryKnowledge
            testCase
                "read output failures distinguish dispatched and unsent attempts"
                readOutputFailure
            testCase
                "partial stdout and stderr failures preserve delivery knowledge"
                partialOutputKnowledge
            testCase "bounded documents and NDJSON reject invalid frames before I/O" readerBounds
            testCase
                "uncertainty ends a session before another frame can overwrite exit four"
                uncertainSession
            testCase "sessions emit one validated reply per line" sessionLines
            testCase "discovery reads the shared catalogue without credentials" offlineDiscovery
        ]
