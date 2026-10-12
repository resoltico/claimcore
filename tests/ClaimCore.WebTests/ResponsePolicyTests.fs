module ClaimCore.WebTests.ResponsePolicyTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Web

let private expected =
    [
        "X-Content-Type-Options", "nosniff"
        "Referrer-Policy", "no-referrer"
        "Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=()"
        "Cross-Origin-Opener-Policy", "same-origin"
        "Cross-Origin-Resource-Policy", "same-origin"
        "X-Frame-Options", "DENY"
        "X-Robots-Tag", "noindex, nofollow"
        "Cache-Control", "no-store, max-age=0"
        "Pragma", "no-cache"
        "Expires", "0"
    ]

let private downstream (context: HttpContext) =
    task {
        let path = context.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""

        if path = "/body" then
            match! HttpBody.readRequest 65536 context with
            | Ok _ -> context.Response.StatusCode <- 200
            | Error reason -> do! (RouteSupport.inputFailure context reason).ExecuteAsync(context)

            return ()

        if path = "/dispatch" then
            RouteSupport.markDispatched context

        if path = "/delivery" then
            RouteSupport.markDispatched context
            RouteSupport.markCompleted context

        if List.contains path [ "/before"; "/dispatch"; "/delivery" ] then
            raise (IOException("SYNTHETIC-PRIVATE claimant token operation path password"))

        context.Response.OnStarting(
            Func<Task>(fun () ->
                context.Response.Headers["X-Frame-Options"] <- "SYNTHETIC-INNER"
                Task.CompletedTask)
        )

        context.Response.Headers.Append("Set-Cookie", "synthetic=value; Secure; HttpOnly")
        context.Response.ContentType <- "text/plain"

        context.Response.StatusCode <-
            if path = "/busy" then 429
            elif path = "/missing" then 404
            else 200

        do! context.Response.WriteAsync("synthetic")

        if path = "/started" then
            raise (IOException("SYNTHETIC-PRIVATE"))
    }
    :> Task

let private startServer () =
    let events = ConcurrentBag<HostDiagnostic>()
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore

    builder.WebHost.ConfigureKestrel(fun options -> options.Listen(IPAddress.Loopback, 0))
    |> ignore

    builder.Services.AddSingleton<HostDiagnosticSink>(fun _ ->
        new HostDiagnosticSink(fun event ->
            events.Add event
            Task.CompletedTask))
    |> ignore

    let application = builder.Build()

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            HttpHeaders.register context
            next.Invoke context)
    )
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RouteSupport.handleFailures))
    |> ignore

    application.Run(RequestDelegate downstream)
    application.StartAsync().GetAwaiter().GetResult()

    application

let private responsePolicy () =
    use application = startServer ()

    try
        use client = new HttpClient(BaseAddress = Uri(application.Urls |> Seq.exactlyOne))

        for path in [ "/ok"; "/missing"; "/busy"; "/before"; "/dispatch"; "/delivery" ] do
            use response = client.GetAsync(path).Result

            for name, value in expected do
                let values =
                    match response.Headers.TryGetValues name with
                    | true, values ->
                        values |> Option.ofObj |> Option.defaultValue Seq.empty |> Seq.toList
                    | _ -> response.Content.Headers.GetValues name |> Seq.toList

                Expect.equal values [ value ] "One final value survives Clear and inner callbacks."

            Expect.isTrue
                (response.Headers.Contains "Content-Security-Policy")
                "CSP survives replacement responses."

            let id = response.Headers.GetValues("X-ClaimCore-Diagnostic-Id") |> Seq.exactlyOne
            Expect.equal id.Length 32 "Opaque diagnostic identity is independent of business keys."
            let body = response.Content.ReadAsStringAsync().Result

            Expect.isFalse
                (body.Contains("SYNTHETIC-PRIVATE", StringComparison.Ordinal))
                "No raw failure details."

            if path = "/ok" then
                Expect.isTrue
                    (response.Headers.Contains "Set-Cookie")
                    "Unowned cookie header survives."

                Expect.equal
                    (string response.Content.Headers.ContentType)
                    "text/plain"
                    "Unowned media survives."

        Expect.throws
            (fun () -> client.GetAsync("/started").GetAwaiter().GetResult() |> ignore)
            "Started failure aborts without a second object."
    finally
        application.StopAsync().GetAwaiter().GetResult()

let private saturatedSink () =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    use sink =
        new HostDiagnosticSink(fun _ ->
            entered.TrySetResult(()) |> ignore
            release.Task :> Task)

    let event =
        {
            Id = String.replicate 32 "a"
            Endpoint = "host"
            Stage = HostFailureStage.Dispatch
            Cause = HostFailureCause.Unexpected
        }

    sink.Report event
    entered.Task.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()

    for _ in 1..10000 do
        sink.Report event

    Expect.isGreaterThan sink.Dropped 9900L "Saturation drops instead of holding a request."
    let started = System.Diagnostics.Stopwatch.StartNew()
    (sink :> IDisposable).Dispose()
    Expect.isLessThan started.Elapsed (TimeSpan.FromSeconds 2.) "Blocked sink has bounded shutdown."
    release.TrySetResult(()) |> ignore

let private stalledKestrelBody () =
    use application = startServer ()

    try
        let origin = Uri(application.Urls |> Seq.exactlyOne)
        use client = new TcpClient()
        client.Connect(IPAddress.Loopback, origin.Port)
        use stream = client.GetStream()

        let head =
            Encoding.ASCII.GetBytes(
                $"POST /body HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nContent-Type: application/json\r\nContent-Length: 50000\r\nConnection: close\r\n\r\nx"
            )

        stream.Write(head)
        use sending = new System.Threading.CancellationTokenSource()

        let upload =
            task {
                try
                    while not sending.IsCancellationRequested do
                        do! stream.WriteAsync(Array.zeroCreate<byte> 4096, sending.Token)
                        do! Task.Delay(1000, sending.Token)
                with
                | :? OperationCanceledException -> ()
                | :? IOException -> ()
            }

        try
            use reader = new StreamReader(stream)
            let started = System.Diagnostics.Stopwatch.StartNew()

            let status =
                reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds 12.).GetAwaiter().GetResult()

            Expect.equal
                status
                ("HTTP/1.1 400 Bad Request": string | null)
                "Absolute acquisition budget refuses an incomplete slow-drip body."

            Expect.isGreaterThan
                started.Elapsed
                (TimeSpan.FromSeconds 9.)
                "The fixture isolates the application deadline while staying above the transport data rate."
        finally
            sending.Cancel()
            upload.GetAwaiter().GetResult()
    finally
        application.StopAsync().GetAwaiter().GetResult()

let tests =
    testList
        "final response policy"
        [
            testCase
                "[CC-WEB-001] real Kestrel slow-drip acquisition ends on its absolute deadline"
                stalledKestrelBody
            testCase
                "[CC-WEB-001] Kestrel runs final headers after reset and inner callbacks"
                responsePolicy
            testCase
                "[CC-WEB-001] blocked host diagnostics stay bounded and non-blocking"
                saturatedSink
        ]
