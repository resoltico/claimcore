module ClaimCore.Tests.ProtocolDiagnosticTests

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts

let private error expected =
    function
    | Ok _ -> failtest "Expected a typed protocol refusal"
    | Error(failure: ProtocolFailure) -> Expect.equal failure.Reason expected "Exact reason"

let private scalarEmission () =
    use document = JsonDocument.Parse("{\"number\":1,\"text\":\"PRIVATE-INPUT\"}")
    let text = document.RootElement.GetProperty("text")

    StrictJson.integerAt "/input/limit" 1 50 text
    |> error ProtocolProblem.ExpectedNumber

    StrictJson.requiredProperty "" "input" text
    |> error ProtocolProblem.ExpectedObject

    StrictJson.boolAt "/input/confirmed" text
    |> error ProtocolProblem.ExpectedBoolean

    StrictJson.integerAt "/input/limit" 2 50 (document.RootElement.GetProperty("number"))
    |> error ProtocolProblem.IntegerRange

let private privateLocations () =
    for source in
        [
            "/PRIVATE-KEY"
            "/input/PRIVATE-KEY"
            "/input/~1private"
            "/input/0"
            "relative"
            String.replicate 9 "/input"
        ] do
        Expect.equal
            (ProtocolLocation.fromPath source |> ProtocolLocation.value)
            ""
            "Unknown names never enter outward paths"

    Expect.equal
        (ProtocolLocation.fromPath "/input/command/values/claimantName"
         |> ProtocolLocation.value)
        "/input/command/values/claimantName"
        "Known location retained"

    use document = JsonDocument.Parse("{\"PRIVATE-KEY\":\"PRIVATE-VALUE\"}")

    match StrictJson.exactProperties "/input" [] document.RootElement with
    | Error failure ->
        Expect.equal failure.Path "/input" "Unknown member reports known containing object"
    | Ok _ -> failtest "Unexpected member must be refused"

let private boundedFrameRefusal () =
    use input = new MemoryStream(Encoding.UTF8.GetBytes("123456789\n{}\n"))

    match FrameReader.readLine 4 input with
    | InputFrame.Failure failure ->
        Expect.equal failure.Reason ProtocolProblem.FrameTooLarge "Oversized frame refused"
        Expect.equal input.Position 5L "Only the first excess byte is consumed"
    | _ -> failtest "Expected oversized frame"

    for source in [ "1234\n"; "1234" ] do
        use exact = new MemoryStream(Encoding.UTF8.GetBytes source)

        match FrameReader.readLine 4 exact with
        | InputFrame.Bytes bytes -> Expect.equal bytes.Length 4 "Exact limit remains admissible"
        | _ -> failtest "Expected exact-limit frame"

let private terminalFrameRefusal () =
    let source = "\n" + String.replicate 131073 "x" + "\n{}\n"
    use input = new MemoryStream(Encoding.UTF8.GetBytes source)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    let exitCode =
        RemoteFrameProcessing.session input output errors System.Threading.CancellationToken.None

    Expect.equal exitCode 2 "Oversized input terminates the session"
    Expect.equal input.Position 131074L "Unread tail is never admitted as another invocation"
    Expect.equal errors.Length 0L "Typed refusal stays on stdout"
    let lines = Encoding.UTF8.GetString(output.ToArray()).TrimEnd().Split('\n')
    Expect.equal lines.Length 2 "Prior frame and terminal refusal each have one response"

    for line, code in List.zip (Array.toList lines) [ "BLANK_FRAME"; "FRAME_TOO_LARGE" ] do
        use document = JsonDocument.Parse line

        Expect.equal
            (document.RootElement.GetProperty("code").GetString())
            code
            "Exact frame outcome"

let private vocabulary () =
    let cases = FSharpType.GetUnionCases typeof<ProtocolProblem>
    Expect.equal cases.Length ProtocolProblems.all.Length "Every native cause is catalogued"

    Expect.isTrue
        (cases |> Array.forall (fun item -> item.GetFields().Length = 0))
        "No source text arguments"

    Expect.isNull
        (typeof<ProtocolFailure>.GetProperty("Message"))
        "Native failure has no writable presentation"

    Expect.equal
        (ProtocolProblems.all
         |> List.map ProtocolProblems.token
         |> Set.ofList
         |> Set.count)
        cases.Length
        "IDs are unique"

let tests =
    testList
        "typed protocol diagnostic boundaries"
        [
            testCase
                "wrong scalar kinds are typed refusals rather than escaping exceptions"
                scalarEmission
            testCase "untrusted property names cannot enter diagnostic locations" privateLocations
            testCase
                "[CC-CLI-003] oversized NDJSON frames stop at the first excess byte"
                boundedFrameRefusal
            testCase
                "[CC-CLI-003] oversized session frames terminate without admitting their tail"
                terminalFrameRefusal
            testCase "protocol vocabulary is closed complete and presentation-free" vocabulary
        ]
