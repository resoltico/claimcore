module ClaimCore.Web.Program

open ClaimCore.Contract

open ClaimCore.Contract.Protocol

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
open ClaimCore.Web.Composition

let private help () =
    printfn "ClaimCore.Web %s — HTTPS human interface" ServiceIdentity.version
    printfn "  ClaimCore.Web                 Start the configured HTTPS host"
    printfn "  ClaimCore.Web help            Show this configuration-free help"
    printfn "  ClaimCore.Web version         Show the compiled product version"
    printfn "  ClaimCore.Web version --json  Show compiled release identity as JSON"

    printfn
        "  ClaimCore.Web probe live     Probe this configured listener without opening a runtime"

    printfn "  ClaimCore.Web probe ready    Probe database reachability and current schema"
    printfn "Required settings (credentials are private file paths):"

    WebSettings.required
    |> List.iter (fun setting -> printfn "  %s" (WebSettings.token setting))

    printfn "Optional settings:"

    WebSettings.optional
    |> List.iter (fun setting -> printfn "  %s" (WebSettings.token setting))

let private writeVersion () =
    let output = Console.OpenStandardOutput()

    output.Write(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
            {|
                product = ServiceIdentity.product
                version = ServiceIdentity.version
                assemblyVersion = ServiceIdentity.assemblyVersion
                fileVersion = ServiceIdentity.fileVersion
            |}
    )

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
        options.Limits.MaxRequestBodySize <- int64 configuration.Admission.MaximumJsonBytes

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
        if Admission.trustedConnection configuration.Binding context then
            do! next.Invoke(context)
        else
            do! writeHostFailure context HostFailureReason.ConnectionRejected
    }
    :> Task)

let private openRuntime (configuration: WebConfiguration) (ct: CancellationToken) =
    task {
        let! opened = ServiceRuntime.openConnection configuration.ConnectionString ct

        match opened with
        | Ok value ->
            try
                value.ConfigureRecoveryProtection(
                    RecoveryProtection.create configuration.KeyRingDirectory
                )

                return value
            with error ->
                value.Dispose()
                return raise error
        | Error "Cancelled" when ct.IsCancellationRequested ->
            return raise (OperationCanceledException(ct))
        | Error reason -> return WebStartupDiagnostics.refuse (WebStartupProblem.RuntimeOpen reason)
    }

let private configuredBuilder
    assets
    (configuration: WebConfiguration)
    oidc
    (runtime: IServiceRuntime)
    =
    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    builder.Logging.ClearProviders() |> ignore
    configureKestrel configuration builder

    configureAntiforgery
        configuration.KeyRingDirectory
        configuration.SessionIdle
        runtime.Sessions
        builder.Services

    AuthMiddleware.configure oidc configuration.SessionAbsolute builder.Services

    RateLimits.configure configuration.Admission builder.Services
    builder.Services.AddAuthorization() |> ignore
    builder

let private configureRoutes
    assets
    (configuration: WebConfiguration)
    (runtime: IServiceRuntime)
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

    let readiness = Readiness(runtime.Ready, application.Lifetime.ApplicationStopping)

    application.Use(Func<HttpContext, RequestDelegate, Task>(HostRoutes.health readiness.Check))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RateLimits.ingress))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(ApiRoutes.ingress))
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore

    HostRoutes.map
        assets
        configuration
        (fun principal action -> runtime.WithActor(principal, action))
        application


let private runtimeLifetime (runtime: IServiceRuntime) =
    { new IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    let! _ = runtime.Shutdown(TimeSpan.FromSeconds 30.)
                    return ()
                }
            )
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
        use! _runtimeLease = Task.FromResult(runtimeLifetime runtime)
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
        printfn "%s" ServiceIdentity.version
        0
    | [ "version"; "--json" ] -> writeVersion ()
    | _ -> WebStartupDiagnostics.refuse WebStartupProblem.UnsupportedInvocation

let private report reason =
    try
        let stream = Console.OpenStandardError()
        stream.Write(WebStartupDiagnostics.encode reason)
        stream.Flush()
    with _ ->
        ()

[<EntryPoint>]
let main arguments =
    try
        dispatch arguments
    with
    | WebStartupException reason ->
        report reason

        if reason = WebStartupProblem.UnsupportedInvocation then
            64
        else
            3
    | error ->
        RequestLog.reportException Console.Error error
        report WebStartupProblem.UnexpectedFailure
        70
