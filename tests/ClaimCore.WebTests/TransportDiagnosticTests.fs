module ClaimCore.WebTests.TransportDiagnosticTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private id (root: JsonElement) =
    root.GetProperty("diagnostic").GetProperty("id").GetString()

let private emission () =
    for input, expected in
        [
            "[]", HttpInputProblem.ExpectedObject
            "{", HttpInputProblem.InvalidJson
            "{}", HttpInputProblem.MissingProperty
            "{\"caseReference\":42}", HttpInputProblem.ExpectedString
            "{\"caseReference\":\"x\",\"PRIVATE-UNKNOWN\":1}", HttpInputProblem.UnknownProperty
            "{\"caseReference\":\"x\",\"caseReference\":\"y\"}", HttpInputProblem.DuplicateProperty
        ] do
        match HttpInput.caseReference (bytes input) with
        | Ok _ -> failtest "Invalid transport input must be refused"
        | Error reason -> Expect.equal reason expected "Exact native cause, not English parsing"

let private boundedReads () =
    use exact = new MemoryStream([| 1uy; 2uy |])
    use large = new MemoryStream([| 1uy; 2uy; 3uy |])

    Expect.equal
        (HttpInput.readBounded 2 exact |> wait)
        (Ok [| 1uy; 2uy |])
        "Inclusive size boundary"

    Expect.equal
        (HttpInput.readBounded 2 large |> wait)
        (Error HttpInputProblem.BodyTooLarge)
        "Streamed over-limit cause"

    use broken =
        { new MemoryStream() with
            override _.ReadAsync
                (_: byte array, _: int, _: int, _: System.Threading.CancellationToken)
                =
                Task.FromException<int>(IOException("PRIVATE-STREAM"))
        }

    Expect.equal
        (HttpInput.readBounded 2 broken |> wait)
        (Error HttpInputProblem.BodyUnreadable)
        "Read failure carries no provider text"

let private classifiedStatus () =
    for reason in HttpInputProblems.all do
        let request = context ""

        use document =
            RouteSupport.inputFailure request reason
            |> execute request
            |> JsonDocument.Parse

        let expected = if reason = HttpInputProblem.BodyTooLarge then 413 else 400
        Expect.equal request.Response.StatusCode expected "Type owns HTTP classification"

        Expect.equal
            (document.RootElement.GetProperty("status").GetInt32())
            expected
            "Body and HTTP agree"

        Expect.equal
            (id document.RootElement)
            (HttpInputProblems.token reason)
            "Specific public identity"

let private invokeFailure phase =
    let request = context ""
    let services = ServiceCollection()
    services.AddOptions() |> ignore
    services.AddLogging() |> ignore
    use provider = services.BuildServiceProvider()
    request.RequestServices <- provider

    let next =
        RequestDelegate(fun value ->
            if phase > 0 then
                RouteSupport.markDispatched value

            if phase > 1 then
                RouteSupport.markCompleted value

            Task.FromException(IOException("PRIVATE-PROVIDER-PATH")))

    RouteSupport.handleFailures request next |> _.GetAwaiter().GetResult()

    let output =
        Encoding.UTF8.GetString((request.Response.Body :?> MemoryStream).ToArray())

    Expect.isFalse
        (output.Contains("PRIVATE-", StringComparison.Ordinal))
        "Never exposes exception details"

    use document = JsonDocument.Parse output
    document.RootElement.Clone()

let private dispatchKnowledge () =
    for phase, expected, knowledge in
        [
            0, "WEB_HOST_BEFORE_DISPATCH_FAILED", Some "NOT_STARTED"
            1, "WEB_HOST_DISPATCH_UNCONFIRMED", Some "STARTED_UNCONFIRMED"
            2, "WEB_HOST_COMPLETED_RESPONSE_FAILED", None
        ] do
        let value = invokeFailure phase
        Expect.equal (id value) expected "Failure preserves its dispatch boundary"

        Expect.equal
            (value.GetProperty("executionPhase").GetString() |> Option.ofObj)
            knowledge
            "No false non-commit or retry guarantee"

let private methodFailure () =
    let request = context ""
    let services = ServiceCollection()
    services.AddOptions() |> ignore
    services.AddLogging() |> ignore
    use provider = services.BuildServiceProvider()
    request.RequestServices <- provider

    RouteSupport.handleFailures
        request
        (RequestDelegate(fun value ->
            value.Response.StatusCode <- 405
            Task.CompletedTask))
    |> _.GetAwaiter().GetResult()

    use document =
        JsonDocument.Parse((request.Response.Body :?> MemoryStream).ToArray())

    Expect.equal
        (id document.RootElement)
        "WEB_HOST_METHOD_REJECTED"
        "Framework method refusal is typed"

    Expect.equal request.Response.StatusCode 405 "Method semantics preserved"

let private publicVocabulary () =
    Expect.equal
        (WebHostFailures.all |> List.map WebHostFailures.token |> Set.ofList |> Set.count)
        WebHostFailures.all.Length
        "Every cause has one unique stable ID"

    for reason in WebHostFailures.all do
        use document = JsonDocument.Parse(WebWireCodec.hostFailure reason)

        Expect.equal
            (id document.RootElement)
            (WebHostFailures.token reason)
            "Actual codec projects owning policy"

        Expect.equal
            (document.RootElement.GetProperty("status").GetInt32())
            (WebHostFailures.status reason)
            "Native policy binds status"

        Expect.equal
            (document.RootElement.GetProperty("diagnostic").GetProperty("parameters").GetRawText())
            "{}"
            "No arbitrary arguments"

let private startupPrivacy () =
    for reason in WebStartupDiagnostics.all do
        use document = JsonDocument.Parse(WebStartupDiagnostics.encode reason)

        Expect.equal
            (id document.RootElement)
            (WebStartupDiagnostics.token reason)
            "Explicit startup identity"

        let parameters =
            document.RootElement.GetProperty("diagnostic").GetProperty("parameters")

        match reason with
        | WebStartupProblem.MissingSetting setting
        | WebStartupProblem.InvalidSetting setting ->
            Expect.equal
                (parameters.GetProperty("setting").GetString())
                (WebSettings.token setting)
                "Known setting token, never supplied value"
        | _ -> Expect.equal (parameters.GetRawText()) "{}" "Provider details cannot enter args"

    Expect.isTrue
        (FSharpType.GetUnionCases(typeof<WebSetting>)
         |> Array.forall (fun item -> item.GetFields().Length = 0))
        "Settings are a closed vocabulary"

let tests =
    testList
        "transport and host diagnostics"
        [
            testCase "strict HTTP readers emit specific closed causes" emission
            testCase "bounded HTTP input distinguishes limit and read failures" boundedReads
            testCase "typed input classification binds diagnostic and HTTP status" classifiedStatus
            testCase "host failures preserve request-local dispatch knowledge" dispatchKnowledge
            testCase "framework method refusal uses the typed host boundary" methodFailure
            testCase "all public host causes have exact parameterless diagnostics" publicVocabulary
            testCase
                "startup diagnostics expose known settings but no supplied values"
                startupPrivacy
        ]
