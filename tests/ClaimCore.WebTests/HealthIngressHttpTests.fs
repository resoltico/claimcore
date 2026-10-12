module ClaimCore.WebTests.HealthIngressHttpTests

open System
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Web

let private observeCommands increment =
    let listener = new ActivityListener()

    listener.ShouldListenTo <-
        Func<ActivitySource, bool>(fun source ->
            source.Name.StartsWith("Npgsql", StringComparison.Ordinal))

    listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)

    listener.ActivityStarted <- Action<Activity>(fun _ -> increment ())

    ActivitySource.AddActivityListener listener

    listener

let private startServer (release: TaskCompletionSource<unit>) entered =
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore

    builder.WebHost.ConfigureKestrel(fun options -> options.Listen(IPAddress.Loopback, 0))
    |> ignore

    builder.Services.AddSingleton<DynamicIngress>() |> ignore
    let application = builder.Build()

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            HttpHeaders.register context
            next.Invoke context)
    )
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(
            HostRoutes.health (fun () -> "SYNTHETIC_ONLY", "ACTIVE", false)
        )
    )
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RateLimits.ingress))
    |> ignore

    application.Run(
        RequestDelegate(fun _ ->
            entered ()
            release.Task :> Task)
    )

    application.StartAsync().GetAwaiter().GetResult()
    application

let private connectProbes port =
    let clients = ResizeArray<TcpClient>()

    try
        for _ in 1..1000 do
            let client = new TcpClient()
            clients.Add client
            client.Connect(IPAddress.Loopback, port)

        clients.ToArray()
    with _ ->
        clients |> Seq.iter _.Dispose()
        reraise ()

let private probe port token (connection: TcpClient) =
    task {
        use client = connection
        use stream = client.GetStream()

        let request =
            Encoding.ASCII.GetBytes(
                $"GET /health/ready HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: synthetic=session\r\nConnection: close\r\n\r\n"
            )

        do! stream.WriteAsync(request, token)
        use reader = new StreamReader(stream)
        let! status = reader.ReadLineAsync(token)

        Expect.equal
            status
            ("HTTP/1.1 503 Service Unavailable": string | null)
            "Synthetic scope is not real-data ready."

        let mutable reading = true
        let mutable scope = false

        while reading do
            let! line = reader.ReadLineAsync(token)

            match line with
            | null
            | "" -> reading <- false
            | line when
                line.Equals(
                    "X-ClaimCore-Data-Use-Scope: SYNTHETIC_ONLY",
                    StringComparison.OrdinalIgnoreCase
                )
                ->
                scope <- true
            | _ -> ()

        Expect.isTrue scope "Cached scope stays truthful."
    }

let private exercise () =
    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable authenticated = 0
    let mutable commands = 0

    use listener =
        observeCommands (fun () -> Interlocked.Increment(&commands) |> ignore)

    use application =
        startServer release (fun () -> Interlocked.Increment(&authenticated) |> ignore)

    use client = new HttpClient(BaseAddress = Uri(application.Urls |> Seq.exactlyOne))
    client.DefaultRequestHeaders.Add("Cookie", "synthetic=session")
    let held = [| for _ in 1..24 -> client.GetAsync("/signin-oidc") |]

    try
        Expect.isTrue
            (SpinWait.SpinUntil((fun () -> Volatile.Read(&authenticated) = 24), 10000))
            "Actual HTTP callback work owns the complete ingress budget."

        use login = client.GetAsync("/auth/login").GetAwaiter().GetResult()

        Expect.equal
            login.StatusCode
            HttpStatusCode.TooManyRequests
            "Refusal precedes authentication."

        // Establish connections before the simultaneous request burst so the operating
        // system's listen backlog is not mistaken for application ingress behaviour.
        let port = client.BaseAddress |> Option.ofObj |> Option.get |> _.Port
        let connected = connectProbes port
        use budget = new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let probes = connected |> Array.map (probe port budget.Token)
        Task.WhenAll(probes).GetAwaiter().GetResult() |> ignore

        Expect.equal
            authenticated
            24
            "Cookie-bearing probes bypass all downstream auth/session/core work."

        Expect.equal commands 0 "Actual driver instrumentation observes no probe database work."

        use prefix = client.GetAsync("/health/ready/extra").GetAwaiter().GetResult()

        Expect.equal
            prefix.StatusCode
            HttpStatusCode.TooManyRequests
            "A health prefix remains dynamic."

        use method = new HttpRequestMessage(HttpMethod.Head, "/health/ready")
        use wrongMethod = client.SendAsync(method).GetAwaiter().GetResult()

        Expect.equal
            wrongMethod.StatusCode
            HttpStatusCode.TooManyRequests
            "Only exact GET is exempt."
    finally
        release.TrySetResult(()) |> ignore
        Task.WhenAll(held).GetAwaiter().GetResult() |> Array.iter _.Dispose()
        application.StopAsync().GetAwaiter().GetResult()

let tests =
    testCase
        "[CC-WEB-001] 1000 concurrent real HTTP probes remain cheap under callback saturation"
        exercise
