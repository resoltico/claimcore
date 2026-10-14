module internal ClaimCore.WebTests.OidcHostFixture

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Web
open ClaimCore.WebTests.FacadeStub
open ClaimCore.WebTests.TestServerFixture

let private configure
    configuration
    root
    (idToken: string option ref)
    (builder: WebApplicationBuilder)
    =
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseTestServer() |> ignore

    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider()
    |> ignore

    AuthMiddleware.configureSyntheticLoopback
        configuration
        root
        (fun token -> idToken.Value <- Some token)
        (TimeSpan.FromMinutes 30.)
        (TimeSpan.FromHours 8.)
        builder.Services

    builder.Services.Configure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
        AuthMiddleware.BearerScheme,
        fun (options: Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions) ->
            options.Events.OnAuthenticationFailed <-
                Func<Microsoft.AspNetCore.Authentication.JwtBearer.AuthenticationFailedContext, Task>
                    (fun context ->
                        printfn "Synthetic JWT failure type: %s" (context.Exception.GetType().Name)
                        Task.CompletedTask)
    )
    |> ignore

    builder.Services.AddAuthorization() |> ignore

    builder.Services.AddAntiforgery(fun options -> options.HeaderName <- "X-ClaimCore-Antiforgery")
    |> ignore

    RateLimits.configure limits builder.Services

let private authenticationRoutes (application: WebApplication) configuration =
    application.MapGet(
        "/auth/state",
        Func<HttpContext, IResult>(fun context ->
            if context.User.Identity |> Option.ofObj |> Option.exists _.IsAuthenticated then
                Results.NoContent()
            else
                Results.Unauthorized())
    )
    |> ignore

    application.MapGet(
        "/auth/bearer",
        Func<HttpContext, Task<IResult>>(fun context ->
            task {
                match! Admission.bearerActor configuration context with
                | Ok _ -> return Results.NoContent()
                | Error _ -> return Results.Unauthorized()
            })
    )
    |> ignore

type Host(configuration: OidcConfiguration, root: X509Certificate2) =
    let assets = Directory.CreateTempSubdirectory("claimcore-oidc-host-").FullName

    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    let idToken = ref None
    let sink = new StringWriter()
    do configure configuration root idToken builder
    let application = builder.Build()

    do
        application.Use(
            Func<HttpContext, RequestDelegate, Task>(fun context next ->
                context.Connection.RemoteIpAddress <- IPAddress.Loopback
                HttpHeaders.register context
                next.Invoke context)
        )
        |> ignore

        application.Use(Func<HttpContext, RequestDelegate, Task>(RequestLog.middleware sink))
        |> ignore

        application.Use(Func<HttpContext, RequestDelegate, Task>(RouteSupport.handleFailures sink))
        |> ignore

        application.Use(Func<HttpContext, RequestDelegate, Task>(ApiRoutes.ingress))
        |> ignore

        application.UseAuthentication() |> ignore
        application.UseRateLimiter() |> ignore
        application.UseAuthorization() |> ignore

        let web =
            { TestServerFixture.configuration root with
                Oidc = Some configuration
            }

        HostRoutes.map assets web (fun _ -> Facade().ActorCore) application
        authenticationRoutes application configuration
        application.StartAsync().GetAwaiter().GetResult()

    let client = application.GetTestClient()
    do client.BaseAddress <- origin
    member _.Client = client
    member _.IdToken = idToken.Value

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            application.StopAsync().GetAwaiter().GetResult()
            application.DisposeAsync().AsTask().GetAwaiter().GetResult()
            sink.Dispose()
            Directory.Delete(assets, true)
