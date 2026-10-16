module ClaimCore.WebTests.TestServerFixture

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.FacadeStub

let origin = Uri "https://localhost:5443"

let oidc =
    {
        Issuer = Uri "https://issuer.example.test/realm"
        TrustRoot = None
        ClientId = "web"
        ClientSecret = "synthetic"
        ApiAudience = "api"
        CliClientId = "cli"
    }

let limits =
    {
        MaximumJsonBytes = 65536
        CorePermitLimit = 4
        CoreQueueLimit = 16
        LoginPermitLimit = 5
    }

let configuration certificate =
    {
        Binding = WebBindings.create origin "127.0.0.1" 5443
        ConnectionString = "synthetic-not-opened"
        Certificate = certificate
        SessionIdle = TimeSpan.FromMinutes 30.
        SessionAbsolute = TimeSpan.FromHours 8.
        Admission = limits
        Oidc = Some oidc
    }

let private services (builder: WebApplicationBuilder) =
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseTestServer() |> ignore

    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider()
    |> ignore

    builder.Services.AddAntiforgery(fun options ->
        options.HeaderName <- "X-ClaimCore-Antiforgery"
        options.Cookie.Name <- "__Host-ClaimCoreAntiforgery"
        options.Cookie.SecurePolicy <- CookieSecurePolicy.Always)
    |> ignore

    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(fun options ->
            options.Cookie.Name <- "__Host-ClaimCoreSession"
            options.Cookie.SecurePolicy <- CookieSecurePolicy.Always

            options.Events.OnRedirectToLogin <-
                Func<RedirectContext<CookieAuthenticationOptions>, Task>(fun context ->
                    context.Response.StatusCode <- 401
                    Task.CompletedTask))
    |> ignore

    builder.Services.AddAuthorization() |> ignore
    RateLimits.configure limits builder.Services

let private pipeline (application: WebApplication) sink readiness =
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

    application.Use(Func<HttpContext, RequestDelegate, Task>(HostRoutes.health readiness))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RateLimits.ingress))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(ApiRoutes.ingress))
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore

let private login (context: HttpContext) : Task =
    task {
        let identity =
            ClaimsIdentity(
                [ System.Security.Claims.Claim("sub", "synthetic-human") ],
                CookieAuthenticationDefaults.AuthenticationScheme
            )

        do!
            context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimsPrincipal identity
            )

        context.Response.StatusCode <- 200
    }

type Host(ready: bool, ?forActor: PrincipalKey -> IActorClaimsCore) =
    let assets = Directory.CreateTempSubdirectory("claimcore-host-").FullName
    let sink = new StringWriter()
    let certificate = ProbeHttpsFixture.certificate ()
    let facade = Facade()

    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    do
        File.WriteAllText(
            Path.Combine(assets, "index.html"),
            "<!doctype html><title>Synthetic ClaimCore</title>"
        )

        services builder

    let application = builder.Build()

    do
        pipeline application sink (fun () -> Task.FromResult ready)
        application.MapPost("/__test/login", RequestDelegate login) |> ignore

        HostRoutes.map
            assets
            (configuration certificate)
            (ClaimCore.Hosting.ServiceGateway.bind (fun principal ->
                facade.Principal <- Some principal

                match forActor with
                | Some factory -> factory principal
                | None -> facade.ActorCore))
            application

        application.StartAsync().GetAwaiter().GetResult()

    let client = application.GetTestClient()
    let mutable cookies = Map.empty<string, string>
    do client.BaseAddress <- origin
    member _.Facade = facade
    member _.Log = sink.ToString()

    member _.ClearLog() =
        sink.GetStringBuilder().Clear() |> ignore

    member _.Send
        (method: HttpMethod, path: string, body: byte array option, headers: (string * string) list)
        =
        task {
            use request = new HttpRequestMessage(method, path)
            request.Headers.Host <- origin.Authority

            request.Headers.TryAddWithoutValidation(
                "Cookie",
                cookies |> Map.toList |> List.map snd |> String.concat "; "
            )
            |> ignore

            for name, value in headers do
                request.Headers.TryAddWithoutValidation(name, value) |> ignore

            body
            |> Option.iter (fun bytes ->
                let content = new ByteArrayContent(bytes) in
                content.Headers.ContentType <- MediaTypeHeaderValue.Parse "application/json"
                request.Content <- content)

            use! response = client.SendAsync request

            match response.Headers.TryGetValues "Set-Cookie" with
            | true, values ->
                for value in nonNull values do
                    let pair = value.Split(';', 2)[0] in
                    cookies <- Map.add (pair.Split('=', 2)[0]) pair cookies
            | _ -> ()

            let! payload = response.Content.ReadAsByteArrayAsync()
            return int response.StatusCode, payload
        }

    member this.Bootstrap() =
        this.Send(
            HttpMethod.Get,
            "/api/session",
            None,
            [
                "ClaimCore-Contract", WireContractFingerprint.value EndpointCatalog.fingerprint
            ]
        )

    member this.Token() =
        task {
            let! _, bytes = this.Bootstrap()
            use doc = JsonDocument.Parse bytes

            return
                doc.RootElement
                    .GetProperty("outcome")
                    .GetProperty("data")
                    .GetProperty("antiforgeryToken")
                    .GetString()
                |> nonNull
        }

    member this.Login() =
        this.Send(HttpMethod.Post, "/__test/login", None, [])

    member this.Call(endpoint, body, token, ?fingerprint: string) =
        this.Send(
            HttpMethod.Post,
            "/api/" + endpoint,
            Some body,
            [
                "Origin", origin.GetLeftPart(UriPartial.Authority)
                "X-ClaimCore-Antiforgery", token
                "ClaimCore-Contract",
                defaultArg fingerprint (WireContractFingerprint.value EndpointCatalog.fingerprint)
            ]
        )

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            application.StopAsync().GetAwaiter().GetResult()
            application.DisposeAsync().AsTask().GetAwaiter().GetResult()
            certificate.Dispose()
            sink.Dispose()
            Directory.Delete(assets, true)
