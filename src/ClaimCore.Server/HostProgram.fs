module ClaimCore.Server.HostProgram

open ClaimCore.Server

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.HostSecurity


open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.StaticFiles
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open ClaimCore.Server.Composition

let private output (bytes: byte array) =
    try
        use stream = StandardSinks.output ()
        stream.Write bytes
        stream.Flush()
    with _ ->
        WebStartupDiagnostics.refuse WebStartupProblem.OutputDeliveryFailed

let private text value =
    System.Text.Encoding.UTF8.GetBytes(value + Environment.NewLine) |> output

let private help () =
    [
        "ClaimCore.Server " + BuildIdentity.current.Version + " — HTTPS human interface"
        "  ClaimCore.Server                 Start the configured HTTPS host"
        "  ClaimCore.Server help            Show this configuration-free help"
        "  ClaimCore.Server version         Show the compiled product version"
        "  ClaimCore.Server version --json  Show compiled release identity as JSON"
        "  ClaimCore.Server probe live      Probe this configured listener without opening a runtime"
        "  ClaimCore.Server probe ready     Probe database reachability and current schema"
        "Required settings (credentials are private file paths):"
        yield!
            WebSettings.required
            |> List.map (fun setting -> "  " + WebSettings.token setting)
        "Optional settings:"
        yield!
            WebSettings.optional
            |> List.map (fun setting -> "  " + WebSettings.token setting)
    ]
    |> String.concat Environment.NewLine
    |> text

let private writeVersion () =
    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
        {|
            product = BuildIdentity.current.Product
            version = BuildIdentity.current.Version
            assemblyVersion = BuildIdentity.current.AssemblyVersion
            fileVersion = BuildIdentity.current.FileVersion
        |}
    |> output

    0

let private assetDirectory () =
    let published = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    let index = Path.Combine(published, "index.html")

    if Directory.Exists(published) && File.Exists(index) then
        published
    else
        WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing

let private writeHostFailure context reason =
    task {
        HttpHeaders.noStore context
        do! (RouteSupport.hostFailure context reason).ExecuteAsync(context)
    }

let private configureKestrel (configuration: WebConfiguration) (builder: WebApplicationBuilder) =
    builder.WebHost.ConfigureKestrel(fun options ->
        options.AddServerHeader <- false
        options.Limits.MaxRequestBodySize <- int64 configuration.RequestGate.MaximumJsonBytes

        options.Listen(
            configuration.Binding.ListenAddress,
            configuration.Binding.ListenPort,
            fun listen -> listen.UseHttps(configuration.Certificate) |> ignore
        ))
    |> ignore

let private configureAntiforgery directory idle sessions (services: IServiceCollection) =
    SessionKeys.configure directory idle sessions services

    services.AddAntiforgery(fun options ->
        options.HeaderName <- "X-ClaimCore-Antiforgery"
        options.Cookie.Name <- "__Host-ClaimCoreAntiforgery"
        options.Cookie.HttpOnly <- true
        options.Cookie.IsEssential <- true
        options.Cookie.Path <- "/"
        options.Cookie.SecurePolicy <- CookieSecurePolicy.Always
        options.Cookie.SameSite <- SameSiteMode.Strict)
    |> ignore

let private verifyOidcMetadata (configuration: OidcConfiguration) (ct: CancellationToken) =
    task {
        try
            use handler =
                configuration.TrustRoot
                |> Option.map AuthMiddleware.syntheticTrustHandler
                |> Option.defaultWith AuthMiddleware.productionTrustHandler

            handler.AllowAutoRedirect <- false
            use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds(10.))
            do! OidcStartup.verify client configuration ct
        with
        | :? HttpRequestException
        | :? InvalidOperationException ->
            return WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid
    }

let private requireTrustedConnection
    (configuration: WebConfiguration)
    (context: HttpContext)
    (next: RequestDelegate)
    : Task =
    (task {
        if RequestGate.trustedConnection configuration.Binding context then
            do! next.Invoke(context)
        else
            do! writeHostFailure context HostFailureReason.ConnectionRejected
    }
    :> Task)

let private openRuntime (configuration: WebConfiguration) (ct: CancellationToken) =
    task {
        let! opened = Runtime.openConnection configuration.ConnectionString ct

        match opened with
        | Ok value -> return value
        | Error RuntimeOpenFault.Cancelled when ct.IsCancellationRequested ->
            return raise (OperationCanceledException(ct))
        | Error reason -> return WebStartupDiagnostics.refuse (WebStartupProblem.RuntimeOpen reason)
    }

let private configuredBuilder assets (configuration: WebConfiguration) oidc (runtime: IRuntime) =
    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    builder.Logging.ClearProviders() |> ignore
    builder.Logging.AddProvider(new FrameworkExceptionLog(Console.Error)) |> ignore
    configureKestrel configuration builder

    configureAntiforgery
        configuration.KeyRingDirectory
        configuration.SessionIdle
        runtime.Sessions
        builder.Services

    AuthMiddleware.configure oidc configuration.SessionAbsolute builder.Services

    RateLimits.configure configuration.RequestGate builder.Services

    builder.Services.AddHostedService<HousekeepingService>(fun _ ->
        new HousekeepingService(
            (fun token ->
                task {
                    let! result = runtime.Housekeep token
                    WebMetrics.sessionsExpired result.SessionsDeleted
                }),
            TimeProvider.System
        ))
    |> ignore

    builder.Services.AddAuthorization() |> ignore
    builder

let private configureRoutes
    assets
    (configuration: WebConfiguration)
    (runtime: IRuntime)
    (application: WebApplication)
    =
    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            HttpHeaders.register context
            next.Invoke context)
    )
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RequestLog.middleware Console.Error))
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(RouteSupport.handleFailures Console.Error)
    )
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(requireTrustedConnection configuration)
    )
    |> ignore

    let publicFiles = HostRoutes.publicAssets assets application

    let readiness = Readiness(runtime.Ready, application.Lifetime.ApplicationStopping)

    application.Use(Func<HttpContext, RequestDelegate, Task>(HostRoutes.health readiness.Check))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RateLimits.ingress))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(ApiRoutes.ingress))
    |> ignore

    HostRoutes.catalogs publicFiles application
    RateLimits.authentication application
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore

    HostRoutes.map
        assets
        configuration
        ClaimCore.Server.ApiRoutes.route
        (fun principal action -> runtime.WithActor(principal, action))
        application


let private report reason =
    try
        use stream = StandardSinks.errors ()
        stream.Write(WebStartupDiagnostics.encode reason)
        stream.Flush()
    with _ ->
        ()

/// Preserve primary work failures and report bounded teardown independently.
let internal withShutdown work shutdown reportShutdown =
    task {
        let! primary =
            task {
                try
                    let! result = work ()
                    return Ok result
                with error ->
                    return
                        Error(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture error)
            }

        let! outcome =
            task {
                try
                    return! shutdown ()
                with _ ->
                    return ShutdownOutcome.TeardownFailed
            }

        if outcome <> ShutdownOutcome.Drained then
            try
                reportShutdown outcome
            with _ ->
                ()

        match primary with
        | Ok result -> return result, outcome
        | Error error ->
            error.Throw()
            return Unchecked.defaultof<_>
    }

let private runAsync (ct: CancellationToken) =
    task {
        let configuration = Configuration.load ()

        use _certificates = Configuration.ownCertificates configuration

        let oidc =
            configuration.Oidc
            |> Option.defaultWith (fun () ->
                WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid)

        do! verifyOidcMetadata oidc ct

        let assets = assetDirectory ()

        let! runtime = openRuntime configuration ct

        let work () =
            task {
                try
                    ct.ThrowIfCancellationRequested()
                    let builder = configuredBuilder assets configuration oidc runtime
                    use application = builder.Build()
                    configureRoutes assets configuration runtime application

                    Console.WriteLine(
                        "ClaimCore Web login URL: "
                        + configuration.Binding.Origin.GetLeftPart(UriPartial.Authority)
                    )

                    do! (application :> IHost).RunAsync(ct)
                    return 0
                with :? OperationCanceledException when ct.IsCancellationRequested ->
                    return 0
            }

        let reportShutdown =
            function
            | ShutdownOutcome.Drained -> ()
            | ShutdownOutcome.BoundExpired -> report WebStartupProblem.RuntimeDrainBoundExpired
            | ShutdownOutcome.TeardownFailed -> report WebStartupProblem.RuntimeTeardownFailed

        let! result, shutdown =
            withShutdown work (fun () -> runtime.Shutdown(TimeSpan.FromSeconds 30.)) reportShutdown

        return if shutdown = ShutdownOutcome.Drained then result else 3
    }

let private run () =
    use lifetime = new WebProcessLifetime()

    try
        runAsync lifetime.Token |> _.GetAwaiter().GetResult()
    with :? OperationCanceledException when lifetime.Token.IsCancellationRequested ->
        0

let private dispatch arguments =
    match arguments |> Array.toList with
    | [] -> run ()
    | [ "probe"; "live" ] -> WebProbe.run "/health/live" |> _.GetAwaiter().GetResult()
    | [ "probe"; "ready" ] -> WebProbe.run "/health/ready" |> _.GetAwaiter().GetResult()
    | [ "help" ]
    | [ "--help" ] ->
        help ()
        0
    | [ "version" ]
    | [ "--version" ] ->
        text BuildIdentity.current.Version
        0
    | [ "version"; "--json" ] -> writeVersion ()
    | _ -> WebStartupDiagnostics.refuse WebStartupProblem.UnsupportedInvocation

let main arguments =
    try
        dispatch arguments
    with
    | WebStartupException reason ->
        report reason

        match reason with
        | WebStartupProblem.UnsupportedInvocation -> 64
        | _ -> 3
    | error ->
        RequestLog.reportException Console.Error error
        report WebStartupProblem.UnexpectedFailure
        70
