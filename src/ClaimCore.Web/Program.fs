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
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Hosting

let private help () =
    printfn "ClaimCore.Web %s — local HTTPS human interface" BuildIdentity.current.Version
    printfn "  ClaimCore.Web                 Start the configured loopback host"
    printfn "  ClaimCore.Web help            Show this configuration-free help"
    printfn "  ClaimCore.Web version         Show the compiled product version"
    printfn "  ClaimCore.Web version --json  Show compiled release identity as JSON"
    printfn "Required private paths:"
    printfn "  CLAIMCORE_CONNECTION_FILE"
    printfn "  CLAIMCORE_WITNESS_CONNECTION_FILE"
    printfn "  CLAIMCORE_WITNESS_KEY_FILE"
    printfn "  CLAIMCORE_SUPPRESSION_KEY_FILE"
    printfn "  CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE"
    printfn "  CLAIMCORE_WEB_CERTIFICATE_PATH"
    printfn "  CLAIMCORE_WEB_STATE_DIR"
    printfn "  CLAIMCORE_OIDC_ISSUER"
    printfn "  CLAIMCORE_OIDC_CLIENT_ID"
    printfn "  CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
    printfn "  CLAIMCORE_OIDC_API_AUDIENCE"
    printfn "  CLAIMCORE_OIDC_CLI_CLIENT_ID"
    printfn "  CLAIMCORE_OIDC_SERVICE_CLIENT_ID"
    printfn "Optional tightening settings:"

    printfn
        "  CLAIMCORE_WEB_ORIGIN                 HTTPS localhost origin; default https://localhost:5443"

    printfn "  CLAIMCORE_WEB_MAX_JSON_BYTES         1..65536; default 65536"
    printfn "  CLAIMCORE_WEB_CORE_PERMITS           1..4; default 4"
    printfn "  CLAIMCORE_WEB_CORE_QUEUE             1..16; default 16"
    printfn "  CLAIMCORE_WEB_LOGIN_PERMITS          1..5 per minute; default 5"
    printfn "  CLAIMCORE_WEB_SESSION_IDLE_MINUTES   1..30; default 30"
    printfn "  CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES 1..480; default 480 and not below idle"

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

        options.ListenLocalhost(
            configuration.Origin.Port,
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

let private verifyOidcMetadata (configuration: OidcConfiguration) =
    try
        use handler =
            configuration.TrustRoot
            |> Option.map AuthMiddleware.syntheticTrustHandler
            |> Option.defaultWith AuthMiddleware.productionTrustHandler

        handler.AllowAutoRedirect <- false
        use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds(10.))

        OidcStartup.verify client configuration
    with
    | :? HttpRequestException
    | :? OperationCanceledException
    | :? InvalidOperationException ->
        WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

let private requireTrustedConnection
    (configuration: WebConfiguration)
    (context: HttpContext)
    (next: RequestDelegate)
    : Task =
    (task {
        HttpHeaders.apply context

        if Admission.trustedConnection configuration.Origin context then
            do! next.Invoke(context)
        else
            do! writeHostFailure context WebHostFailure.ConnectionRejected
    }
    :> Task)

let private openRuntime (configuration: WebConfiguration) =
    match
        Runtime
            .OpenPostgres(
                configuration.ConnectionString,
                configuration.WitnessConnectionString,
                configuration.WitnessKeyRingPath,
                configuration.SuppressionKeyPath,
                configuration.RecoveryArtifactKeyPath,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()
    with
    | Ok value -> value
    | Error reason -> WebStartupDiagnostics.refuse (WebStartupProblem.RuntimeOpen reason)

let private run () =
    let configuration = Configuration.load ()

    use _certificates = Configuration.ownCertificates configuration

    let oidc =
        configuration.Oidc
        |> Option.defaultWith (fun () ->
            WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid)

    verifyOidcMetadata oidc

    use _stateLease = Security.acquireStateDirectory configuration.StateDirectory
    let assets = assetDirectory ()

    use runtime = openRuntime configuration

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
    use application = builder.Build()

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

    Console.WriteLine(
        "ClaimCore Web login URL: "
        + configuration.Origin.GetLeftPart(UriPartial.Authority)
    )

    application.Run()
    0

let private dispatch arguments =
    match arguments |> Array.toList with
    | [] -> run ()
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
