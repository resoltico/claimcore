module ClaimCore.WebTests.TransportGuards

open ClaimCore.Wire

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ClaimCore.Web

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
    HttpHeaders.apply context
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
        context
        (RequestDelegate(fun _ ->
            Task.FromException(InvalidOperationException "sensitive-exception-message")))
    |> _.GetAwaiter().GetResult()

    use response =
        System.Text.Json.JsonDocument.Parse((context.Response.Body :?> MemoryStream).ToArray())

    let actual = response.RootElement.GetProperty("executionPhase")

    Expect.equal
        (actual.ValueKind = System.Text.Json.JsonValueKind.Null)
        (phase = None)
        "Completed response does not invent execution knowledge"

    phase
    |> Option.iter (fun expected -> Expect.equal (actual.GetString()) expected "Dispatch knowledge")

    Expect.isFalse (sink.ToString().Contains "sensitive-exception-message") "Sanitized failure"

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
            testCase "completed response failures preserve execution knowledge" (fun () ->
                failurePhase true true None)
        ]
    |> testSequenced
