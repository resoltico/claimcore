module ClaimCore.ServerTests.TransportGuards

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.IO
open System.Diagnostics.Metrics
open System.Text.Json
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ClaimCore.Server

type private StalledStream() =
    inherit MemoryStream()

    override _.ReadAsync(_, _, _, token) =
        task {
            do! Task.Delay(Timeout.Infinite, token)
            return 0
        }

type private FailedStream() =
    inherit MemoryStream()

    override _.ReadAsync(_, _, _, _) =
        Task.FromException<int>(IOException "untrusted-stream-message")

let private bodyBounds () =
    for size, expected in [ 16, true; 17, false ] do
        use stream = new MemoryStream(Array.zeroCreate<byte> size)

        let result =
            HttpBody.readBounded 16 stream CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Expect.equal (Result.isOk result) expected "Exact byte limit"

let private bodyFailure () =
    use stream = new FailedStream()

    Expect.equal
        (HttpBody.readBounded 16 stream CancellationToken.None
         |> _.GetAwaiter().GetResult())
        (Error BodyProblem.BodyUnreadable)
        "I/O failure is typed without message"

let private bodyCancellation () =
    use stream = new StalledStream()
    use stop = new CancellationTokenSource()
    let pending = HttpBody.readBounded 16 stream stop.Token
    stop.Cancel()

    Expect.equal
        (pending.WaitAsync(TimeSpan.FromSeconds 1.) |> _.GetAwaiter().GetResult())
        (Error BodyProblem.BodyCancelled)
        "No acquisition after cancellation"

let private bodyDeadline () =
    use stream = new StalledStream()
    let pending = HttpBody.readBounded 16 stream CancellationToken.None

    Expect.equal
        (pending.WaitAsync(TimeSpan.FromSeconds 12.) |> _.GetAwaiter().GetResult())
        (Error BodyProblem.BodyCancelled)
        "Absolute acquisition deadline"

let private headers () =
    let context = DefaultHttpContext()
    HttpHeaders.apply (Uri "https://localhost:5443") context
    HttpHeaders.noStore context

    Expect.equal
        (string context.Response.Headers.XContentTypeOptions)
        "nosniff"
        "Content-type boundary"

    Expect.isTrue
        ((string context.Response.Headers.ContentSecurityPolicy).Contains "default-src 'none'")
        "Closed content policy"

    Expect.isTrue
        ((string context.Response.Headers.CacheControl).Contains "no-store")
        "No case response caching"

let private capacity () =
    use limiter = new DynamicIngress()
    let leases = [ for _ in 1..24 -> limiter.Acquire() ]

    try
        Expect.isTrue (leases |> List.forall _.IsAcquired) "Declared budget admitted"
        use refused = limiter.Acquire()
        Expect.isFalse refused.IsAcquired "No excess admission"
    finally
        leases |> List.iter _.Dispose()

let private failurePhase dispatched completed phase =
    let context = DefaultHttpContext()
    context.Response.Body <- new MemoryStream()

    if dispatched then
        RouteSupport.markDispatched context

    if completed then
        RouteSupport.markCompleted context

    use sink = new StringWriter()

    RouteSupport.handleFailures
        sink
        System.Threading.CancellationToken.None
        context
        (RequestDelegate(fun _ ->
            Task.FromException(InvalidOperationException "sensitive-exception-message")))
    |> _.GetAwaiter().GetResult()

    use response =
        System.Text.Json.JsonDocument.Parse((context.Response.Body :?> MemoryStream).ToArray())

    let actual = response.RootElement.GetProperty("hostFailure").GetProperty("phase")

    Expect.equal
        (actual.ValueKind = System.Text.Json.JsonValueKind.Null)
        (phase = None)
        "Completed response does not invent execution knowledge"

    phase
    |> Option.iter (fun expected -> Expect.equal (actual.GetString()) expected "Dispatch knowledge")

    Expect.isFalse (sink.ToString().Contains "sensitive-exception-message") "Sanitized failure"

let private requestOutcome abort stop =
    use disconnected = new CancellationTokenSource()
    use stopping = new CancellationTokenSource()
    use sink = new StringWriter()
    use body = new MemoryStream()
    use diagnostics = StorageFailureLog.attach sink
    use listener = new MeterListener()
    let results = ResizeArray<string>()

    listener.InstrumentPublished <-
        fun instrument observer ->
            if instrument.Name = "claimcore.requests" then
                observer.EnableMeasurementEvents instrument

    listener.SetMeasurementEventCallback<int64>(
        MeasurementCallback<int64>(fun _ _ tags _ ->
            for i in 0 .. tags.Length - 1 do
                if tags[i].Key = "result" then
                    results.Add(string tags[i].Value))
    )

    listener.Start()

    let context = DefaultHttpContext()

    context.Request.Path <-
        PathString(Api.forActor (ActorEndpoint.Query QueryTaskId.WorkViews) |> Api.path)

    context.RequestAborted <- disconnected.Token
    context.Response.Body <- body

    let dispatch =
        RequestDelegate(fun current ->
            RouteSupport.markDispatched current

            if stop then
                stopping.Cancel()

            if abort then
                disconnected.Cancel()
                let error = OperationCanceledException(disconnected.Token)
                ClaimCore.Application.Storage.FailureDiagnostics.report error
                Task.FromException error
            else
                Task.FromException(InvalidOperationException "synthetic dispatch fault"))

    let handle =
        RequestDelegate(fun current ->
            RouteSupport.handleFailures sink stopping.Token current dispatch)

    RequestLog.middleware sink context handle |> _.GetAwaiter().GetResult()

    let records =
        sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)

    let kinds =
        records
        |> Array.choose (fun record ->
            use parsed = JsonDocument.Parse record

            match parsed.RootElement.TryGetProperty "kind" with
            | true, kind -> Some(kind.GetString())
            | _ -> None)

    use logged = JsonDocument.Parse(Array.last records)

    if abort && not stop then
        Expect.equal
            (logged.RootElement.GetProperty("outcome").GetString())
            "ABANDONED"
            "Request outcome"

        Expect.sequenceEqual results [ "abandoned" ] "Request metric"
        Expect.hasLength records 1 "No storage or exception diagnostics"
        Expect.equal body.Length 0L "No failure response was written"
    else
        Expect.equal
            (logged.RootElement.GetProperty("outcome").GetString())
            "HOST_FAILURE"
            "Request outcome"

        Expect.sequenceEqual results [ "host_failure" ] "Request metric"
        Expect.contains kinds "exception" "Exception is logged"

        if stop then
            Expect.contains kinds "storageFailure" "Shutdown cancellation is logged"
        else
            Expect.isGreaterThan body.Length 0L "An open request receives the failure response"

[<Tests>]
let tests =
    testList
        "transport guards"
        [
            testCase "bounded body admits the exact limit and refuses one byte beyond" bodyBounds
            testCase "body I/O failure has a value-free typed outcome" bodyFailure
            testCase "caller cancellation ends stalled body acquisition" bodyCancellation
            testCase "stalled body acquisition has an absolute deadline" bodyDeadline
            testCase "security and no-store headers remain closed" headers
            testCase "dynamic ingress budget has an excess-admission rejecting control" capacity
            testCase "before-dispatch and dispatched failure knowledge remain distinct" (fun () ->
                failurePhase false false (Some "NOT_STARTED")
                failurePhase true false (Some "STARTED_UNCONFIRMED"))
            testCase "abandoned dispatch records only the request and abandonment metric" (fun () ->
                requestOutcome true false)
            testCase
                "open dispatch failures and shutdown cancellations remain host failures"
                (fun () ->
                    requestOutcome false false
                    requestOutcome true true)
            testCase "completed response failures preserve execution knowledge" (fun () ->
                failurePhase true true None)
        ]
    |> testSequenced
