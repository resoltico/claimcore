module ClaimCore.Web.Program

open System
open System.IO
open System.Security.Claims
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.RateLimiting
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
    printfn "  CLAIMCORE_WEB_CERTIFICATE_PATH"
    printfn "  CLAIMCORE_WEB_STATE_DIR"
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
        invalidOp "Publish the verified ClaimCore Web asset manifest before starting ClaimCore.Web."

let private writeHostFailure context status code message =
    task {
        HttpHeaders.noStore context
        do! (WebWire.hostFailure status code message None).ExecuteAsync(context)
    }

let private configureRateLimits (configuration: WebConfiguration) (services: IServiceCollection) =
    services.AddRateLimiter(fun options ->
        options.RejectionStatusCode <- StatusCodes.Status429TooManyRequests

        options.OnRejected <-
            Func<OnRejectedContext, CancellationToken, ValueTask>(fun rejected _ ->
                ValueTask(
                    writeHostFailure
                        rejected.HttpContext
                        StatusCodes.Status429TooManyRequests
                        "WEB_BUSY"
                        "Request admission is busy."
                ))

        options.AddConcurrencyLimiter(
            "core",
            fun limiter ->
                limiter.PermitLimit <- configuration.Admission.CorePermitLimit
                limiter.QueueProcessingOrder <- QueueProcessingOrder.OldestFirst
                limiter.QueueLimit <- configuration.Admission.CoreQueueLimit
        )
        |> ignore

        options.AddFixedWindowLimiter(
            "login",
            fun limiter ->
                limiter.PermitLimit <- configuration.Admission.LoginPermitLimit
                limiter.Window <- TimeSpan.FromMinutes(1.)
                limiter.QueueLimit <- 0
        )
        |> ignore)
    |> ignore

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

let private configureAuthentication (services: IServiceCollection) =
    services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(fun options ->
            options.Cookie.Name <- "__Host-ClaimCoreSession"
            options.Cookie.HttpOnly <- true
            options.Cookie.IsEssential <- true
            options.Cookie.SecurePolicy <- CookieSecurePolicy.Always
            options.Cookie.SameSite <- SameSiteMode.Strict
            options.Cookie.Path <- "/"
            options.SlidingExpiration <- false

            options.Events.OnRedirectToLogin <-
                Func<RedirectContext<CookieAuthenticationOptions>, Task>(fun context ->
                    context.Response.StatusCode <- StatusCodes.Status401Unauthorized
                    Task.CompletedTask)

            options.Events.OnRedirectToAccessDenied <-
                Func<RedirectContext<CookieAuthenticationOptions>, Task>(fun context ->
                    context.Response.StatusCode <- StatusCodes.Status403Forbidden
                    Task.CompletedTask))
    |> ignore

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
            do!
                writeHostFailure
                    context
                    StatusCodes.Status403Forbidden
                    "WEB_CONNECTION_REJECTED"
                    "Connection was refused."
    }
    :> Task)

let private run () =
    let configuration = Configuration.load ()
    use _stateLease = Security.acquireStateDirectory configuration.StateDirectory
    use bootstrapLease = Security.rotateBootstrapCredential configuration.StateDirectory
    let bootstrap = bootstrapLease.Credential
    let assets = assetDirectory ()

    let sessions =
        SessionRegistry(configuration.SessionIdle, configuration.SessionAbsolute)

    use runtime =
        match
            Runtime
                .OpenPostgres(configuration.ConnectionString, CancellationToken.None)
                .GetAwaiter()
                .GetResult()
        with
        | Ok value -> value
        | Error _ -> invalidOp "ClaimCore Web could not open the configured application runtime."

    let webRuntime = WebRuntime.fromRuntime runtime

    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    builder.Logging.ClearProviders() |> ignore
    configureKestrel configuration builder
    configureAntiforgery builder.Services
    configureAuthentication builder.Services
    configureRateLimits configuration builder.Services
    builder.Services.AddAuthorization() |> ignore
    let application = builder.Build()

    application.Lifetime.ApplicationStopping.Register(Action(fun () -> sessions.RevokeAll()))
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(requireTrustedConnection configuration)
    )
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore
    HostRoutes.map assets configuration bootstrap sessions webRuntime application

    Console.WriteLine(
        "ClaimCore Web login URL: "
        + configuration.Origin.GetLeftPart(UriPartial.Authority)
    )

    Console.WriteLine("ClaimCore Web credential file: " + bootstrap.Path)
    application.Run()
    0

[<EntryPoint>]
let main arguments =
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
    | _ ->
        eprintfn "Use ClaimCore.Web help for supported arguments."
        64
