module ClaimCore.Web.Program

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.StaticFiles
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Hosting

let private help () =
    printfn "ClaimCore.Web %s — HTTPS human interface" BuildIdentity.current.Version
    printfn "  ClaimCore.Web                 Start the configured HTTPS host"
    printfn "  ClaimCore.Web help            Show this configuration-free help"
    printfn "  ClaimCore.Web version         Show the compiled product version"
    printfn "  ClaimCore.Web version --json  Show compiled release identity as JSON"

    printfn
        "  ClaimCore.Web probe live     Probe this configured listener without opening a runtime"

    printfn "  ClaimCore.Web probe ready    Probe independently qualified real-data readiness"
    printfn "Required settings (credentials are private file paths):"

    WebSettings.required
    |> List.iter (fun setting -> printfn "  %s" (WebSettings.token setting))

    printfn "Optional settings:"

    WebSettings.optional
    |> List.iter (fun setting -> printfn "  %s" (WebSettings.token setting))

    printfn "Conditional real-data evidence settings:"

    WebSettings.realData
    |> List.iter (fun setting -> printfn "  %s" (WebSettings.token setting))

let private writeVersion () =
    let output = Console.OpenStandardOutput()
    output.Write(BuildIdentityCodec.bytes BuildIdentity.current)
    0

let private assetDirectory () =
    let published = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    let manifest = Path.Combine(published, "claimcore-assets.manifest.json")

    if Directory.Exists(published) && File.Exists(manifest) then
        published
    else
        WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing

let private writeHostFailure context reason =
    task {
        HttpHeaders.noStore context
        do! (WebWire.hostFailure reason).ExecuteAsync(context)
    }

let private configureKestrel (configuration: WebConfiguration) (builder: WebApplicationBuilder) =
    builder.WebHost.ConfigureKestrel(fun options ->
        options.AddServerHeader <- false
        // The raw envelope import is the largest intentional request. Every endpoint still enforces
        // its own tighter allocation bound before it reads the body.
        options.Limits.MaxRequestBodySize <- 131072L

        options.Listen(
            configuration.Binding.ListenAddress,
            configuration.Binding.ListenPort,
            fun listen -> listen.UseHttps(configuration.Certificate) |> ignore
        ))
    |> ignore

let private configureAntiforgery (services: IServiceCollection) =
    services
        .AddDataProtection()
        .SetApplicationName("ClaimCore.Web")
        .UseEphemeralDataProtectionProvider()
    |> ignore

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
        HttpHeaders.apply context

        if Admission.trustedConnection configuration.Binding context then
            do! next.Invoke(context)
        else
            do! writeHostFailure context WebHostFailure.ConnectionRejected
    }
    :> Task)

let private openRuntime (configuration: WebConfiguration) (ct: CancellationToken) =
    task {
        let! opened =
            Runtime.OpenPostgres(
                configuration.ConnectionString,
                configuration.WitnessConnectionString,
                configuration.WitnessKeyRingPath,
                configuration.SuppressionKeyPath,
                configuration.RecoveryArtifactKeyPath,
                ct
            )

        match opened with
        | Ok value -> return value
        | Error RuntimeOpenFault.RuntimeCancelled when ct.IsCancellationRequested ->
            return raise (OperationCanceledException(ct))
        | Error reason -> return WebStartupDiagnostics.refuse (WebStartupProblem.RuntimeOpen reason)
    }

let private configuredBuilder assets (configuration: WebConfiguration) oidc =
    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    builder.Logging.ClearProviders() |> ignore
    configureKestrel configuration builder
    configureAntiforgery builder.Services

    AuthMiddleware.configure
        oidc
        configuration.SessionIdle
        configuration.SessionAbsolute
        builder.Services

    RateLimits.configure configuration.Admission builder.Services
    builder.Services.AddAuthorization() |> ignore
    builder

let private configureRoutes
    assets
    (configuration: WebConfiguration)
    (runtime: Runtime)
    (application: WebApplication)
    =
    application.Use(Func<HttpContext, RequestDelegate, Task>(RouteSupport.handleFailures))
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(requireTrustedConnection configuration)
    )
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore

    HostRoutes.map
        assets
        configuration
        (fun principal -> runtime.ForActor principal)
        (fun ct -> runtime.DataUseReadiness(ct))
        application


let private runAsync (ct: CancellationToken) =
    task {
        let configuration = Configuration.load ()

        use _certificates = Configuration.ownCertificates configuration

        let oidc =
            configuration.Oidc
            |> Option.defaultWith (fun () ->
                WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid)

        do! verifyOidcMetadata oidc ct

        use _stateLease = Security.acquireStateDirectory configuration.StateDirectory
        let assets = assetDirectory ()

        let! runtime = openRuntime configuration ct
        use! _runtimeLease = Task.FromResult(runtime :> IAsyncDisposable)
        ct.ThrowIfCancellationRequested()

        let builder = configuredBuilder assets configuration oidc
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
        printfn "%s" BuildIdentity.current.Version
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
    | _ ->
        report WebStartupProblem.UnexpectedFailure
        70
