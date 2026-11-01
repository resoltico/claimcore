module ClaimCore.TestSupport.TestServerFixture

open ClaimCore.Protocol
open ClaimCore.Domain

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.TestSupport.HandlerStub

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
        CoreQueueLimit = 8
        LoginPermitLimit = 5
    }

let configuration certificate =
    {
        KeyRingDirectory = DirectoryInfo(Path.GetTempPath())
        Binding = WebBindings.create origin "127.0.0.1" 5443
        ConnectionString = "synthetic-not-opened"
        Certificate = certificate
        SessionIdle = TimeSpan.FromMinutes 30.
        SessionAbsolute = TimeSpan.FromHours 8.
        RequestGate = limits
        Oidc = Some oidc
    }

let private services (builder: WebApplicationBuilder) sessions =
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseTestServer() |> ignore

    builder.Services.AddDataProtection() |> ignore

    builder.Services.Replace(
        ServiceDescriptor.Singleton<IDataProtectionProvider>(EphemeralDataProtectionProvider())
    )
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

    sessions
    |> Option.iter (fun storage ->
        builder.Services.AddSingleton<
            Microsoft.Extensions.Options.IPostConfigureOptions<CookieAuthenticationOptions>
         >
            (fun provider ->
                let protection = provider.GetRequiredService<IDataProtectionProvider>()

                Microsoft.Extensions.Options.PostConfigureOptions<CookieAuthenticationOptions>(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    fun options ->
                        options.SessionStore <-
                            OidcTicketStore(
                                TimeSpan.FromMinutes 30.,
                                TimeSpan.FromHours 8.,
                                storage,
                                protection,
                                ignore
                            )
                )
                :> Microsoft.Extensions.Options.IPostConfigureOptions<CookieAuthenticationOptions>)
        |> ignore)

    builder.Services.AddAuthorization() |> ignore
    RateLimits.configure limits builder.Services

let private pipeline assets (application: WebApplication) sink readiness =
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

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(HostRoutes.health (fun _ -> readiness ()))
    )
    |> ignore

    HostRoutes.publicAssets assets application

    application.Use(Func<HttpContext, RequestDelegate, Task>(RateLimits.ingress))
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(ApiRoutes.ingress))
    |> ignore

    RateLimits.authentication application
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

type Host
    (
        ready: bool,
        ?forActor: PrincipalKey -> ServiceContext,
        ?installationId: Guid,
        ?sessions: ClaimCore.Postgres.ISessionStore,
        ?buildAssets: (string * byte array) list
    ) =
    let assets = Directory.CreateTempSubdirectory("claimcore-host-").FullName
    let sink = new StringWriter()
    let certificate = ProbeHttpsFixture.certificate ()
    let facade = Stub()
    let installationId = defaultArg installationId HandlerStub.operationId

    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    do
        File.WriteAllText(
            Path.Combine(assets, "index.html"),
            "<!doctype html><title>Synthetic ClaimCore</title>"
        )

        let inventory =
            defaultArg buildAssets []
            |> List.map (fun (path, bytes) ->
                let file = Path.Combine(assets, path)
                Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
                File.WriteAllBytes(file, bytes)

                {|
                    path = "/" + path
                    sha256 = Convert.ToHexStringLower(SHA256.HashData bytes)
                |})

        File.WriteAllBytes(
            Path.Combine(assets, "public-assets.json"),
            JsonSerializer.SerializeToUtf8Bytes {| schema = 1; assets = inventory |}
        )

        services builder sessions

    let application = builder.Build()

    do
        pipeline assets application sink (fun () -> Task.FromResult ready)
        application.MapPost("/__test/login", RequestDelegate login) |> ignore

        HostRoutes.map
            assets
            (configuration certificate)
            (if forActor.IsSome then
                 ClaimCore.Web.ApiRoutes.route
             else
                 facade.Handlers)
            (fun principal action ->
                task {
                    if not facade.RuntimeAvailable then
                        return None
                    else
                        facade.Principal <- Some principal

                        let actor =
                            match forActor with
                            | Some factory -> factory principal
                            | None -> facade.InvocationContext principal

                        let! result = action actor
                        return Some result
                })
            application

        application.StartAsync().GetAwaiter().GetResult()

    let client = application.GetTestClient()
    let mutable cookies = Map.empty<string, string>
    do client.BaseAddress <- origin
    member _.Client = client
    member _.AssetsDirectory = assets
    member _.Services = application.Services
    member _.HandlerStub = facade
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
            [ "ClaimCore-Contract", ContractIdentity.value ]
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

    member this.Call(endpoint, body, token, ?fingerprint: string, ?installation: string) =
        this.Send(
            HttpMethod.Post,
            "/api/" + endpoint,
            Some body,
            [
                "Origin", origin.GetLeftPart(UriPartial.Authority)
                "X-ClaimCore-Antiforgery", token
                "ClaimCore-Contract", defaultArg fingerprint (ContractIdentity.value)
                "ClaimCore-Installation", defaultArg installation (installationId.ToString("D"))
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
