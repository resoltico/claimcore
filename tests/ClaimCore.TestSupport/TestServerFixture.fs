module ClaimCore.TestSupport.TestServerFixture

open ClaimCore.Wire
open ClaimCore.Presentation
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
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Server
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
        ServiceClientIds = Set.singleton "service"
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
        WitnessConnectionString = "synthetic-not-opened"
        DataKeyFile = "synthetic-not-opened"
        Certificate =
            {
                Leaf = certificate
                Chain = Security.Cryptography.X509Certificates.X509Certificate2Collection()
            }
        SessionIdle = TimeSpan.FromMinutes 30.
        SessionAbsolute = TimeSpan.FromHours 8.
        RequestGate = limits
        Oidc = Some oidc
        Digest = None
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
                                oidc.Issuer,
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

let private pipeline files (application: WebApplication) sink readiness =
    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            context.Connection.RemoteIpAddress <- IPAddress.Loopback
            HttpHeaders.register origin context
            next.Invoke context)
    )
    |> ignore

    application.Use(Func<HttpContext, RequestDelegate, Task>(RequestLog.middleware sink))
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(
            RouteSupport.handleFailures sink application.Lifetime.ApplicationStopping
        )
    )
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(HostRoutes.health (fun _ -> readiness ()))
    )
    |> ignore

    HostRoutes.publicAssets files application |> ignore

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
                [
                    System.Security.Claims.Claim(
                        "sub",
                        (if context.Request.Query.ContainsKey "subject" then
                             context.Request.Query["subject"].ToString()
                         else
                             "synthetic-human")
                    )
                ],
                CookieAuthenticationDefaults.AuthenticationScheme
            )

        // Synthetic authenticated hints exercise production claim propagation; this route exists only in tests.
        for name in [ "name"; "preferred_username"; "email" ] do
            if context.Request.Query.ContainsKey name then
                identity.AddClaim(Claim(name, context.Request.Query[name].ToString()))

        let properties = AuthenticationProperties()

        let cookies =
            context.RequestServices
                .GetRequiredService<
                    Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>
                  >()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme)

        if isNull cookies.SessionStore then
            properties.Items[AuthMiddleware.idTokenProperty] <- "synthetic-logout-hint"

        do!
            context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimsPrincipal identity,
                properties
            )

        context.Response.StatusCode <- 200
    }

let publishedInventory directory (buildAssets: (string * byte array) list) =
    let sha256 (bytes: byte array) = SHA256.HashData bytes
    let script = "document.querySelector('canvas').width = 1;"

    let html =
        $"<!doctype html><html><head></head><body><canvas></canvas><script>{script}</script></body></html>"
        |> Encoding.UTF8.GetBytes

    File.WriteAllBytes(Path.Combine(directory, "attachment-preview.viewer"), html)

    let scripts = "assets/test-handshake.js"

    let entry markup =
        markup + $"<script src=\"/{scripts}\"></script>"

    let documents =
        [
            "index.html", entry "<!doctype html><title>Synthetic ClaimCore</title>"
            "auth-recovery.html", entry "<!doctype html><main>Synthetic recovery</main>"
            scripts, $"globalThis.fixtureContract = \"{ContractIdentity.value}\";"
            "LICENSE.txt", "Synthetic license"
            "PROJECT-NOTICE.md", "# Synthetic project notice"
            "LICENSE-COMMERCIAL.md", "# Synthetic commercial notice"
            "THIRD-PARTY-NOTICES.txt", "Synthetic dependency notices"
            "SOURCE.tar.gz", "Synthetic source bytes"
        ]
        |> List.map (fun (path, text) -> path, Encoding.UTF8.GetBytes text)

    let root = Path.Combine(RepositoryRoot.find (), "web", "locales")

    let source language =
        [
            NativeCatalog.core language
            File.ReadAllBytes(Path.Combine(root, language + ".json"))
        ]

    let catalogs =
        LocalizationPolicy.languages
        |> List.map (fun language ->
            let entries, _ = LocalizationPolicy.load language (source "en") (source language)
            let bytes = LocalizationPolicy.browserCatalog (Map.toList entries)
            let hash = sha256 bytes |> Convert.ToHexStringLower
            language, ($"assets/catalogs/{language}.{hash}.json", bytes))

    let catalogManifest =
        catalogs
        |> List.map (fun (language, (path, _)) -> language, "/" + path)
        |> Map.ofList
        |> JsonSerializer.SerializeToUtf8Bytes

    let assets =
        documents
        @ [ "catalog-manifest.json", catalogManifest ]
        @ List.map snd catalogs
        @ buildAssets
        |> Map.ofList
        |> Map.toList
        |> List.map (fun (path, bytes) ->
            let file = Path.Combine(directory, path)
            Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
            File.WriteAllBytes(file, bytes)

            {|
                path = "/" + path
                sha256 = sha256 bytes |> Convert.ToHexStringLower
            |})

    File.WriteAllBytes(
        Path.Combine(directory, "public-assets.json"),
        JsonSerializer.SerializeToUtf8Bytes
            {|
                schema = 5
                identityChunks = [ "/" + scripts ]
                graph = Map.ofList [ "/" + scripts, Array.empty<string> ]
                assets = assets
                viewer =
                    {|
                        resources = Array.empty<string>
                        sha256 = sha256 html |> Convert.ToHexStringLower
                        scriptSha256 =
                            script |> Encoding.UTF8.GetBytes |> sha256 |> Convert.ToBase64String
                    |}
            |}
    )

type Host
    (
        ready: bool,
        ?forActor: PrincipalKey -> ServiceContext,
        ?installationId: Guid,
        ?sessions: ClaimCore.Application.ISessionStore,
        ?buildAssets: (string * byte array) list,
        ?serviceSetup: IServiceCollection -> unit
    ) =
    let assets = Directory.CreateTempSubdirectory("claimcore-host-").FullName
    let sink = new StringWriter()
    let certificate = ProbeHttpsFixture.certificate ()
    let facade = Stub()
    let installationId = defaultArg installationId HandlerStub.operationId

    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    do
        publishedInventory assets (defaultArg buildAssets [])
        services builder sessions
        serviceSetup |> Option.iter (fun configure -> configure builder.Services)

    let files = PublicAssets.Inventory assets
    let application = builder.Build()

    do
        pipeline files application sink (fun () -> Task.FromResult ready)
        application.MapPost("/__test/login", RequestDelegate login) |> ignore

        HostRoutes.map
            files
            (configuration certificate)
            (Some(Uri "https://issuer.example.test/logout"))
            (if forActor.IsSome then
                 ClaimCore.Server.ApiRoutes.route
             else
                 facade.Handlers)
            (fun principal _ action ->
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

    member _.SendResponse
        (
            method: HttpMethod,
            path: string,
            body: byte array option,
            headers: (string * string) list,
            ?cancellationToken: System.Threading.CancellationToken
        ) =
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

            let! response =
                client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    defaultArg cancellationToken System.Threading.CancellationToken.None
                )

            match response.Headers.TryGetValues "Set-Cookie" with
            | true, values ->
                for value in nonNull values do
                    let pair = value.Split(';', 2)[0] in
                    cookies <- Map.add (pair.Split('=', 2)[0]) pair cookies
            | _ -> ()

            return response
        }

    member this.Send(method, path, body, headers) =
        task {
            use! response = this.SendResponse(method, path, body, headers)
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

    member this.CallResponse
        (
            endpoint,
            body,
            token,
            ?fingerprint: string,
            ?installation: string,
            ?representation: Representation,
            ?cancellationToken: System.Threading.CancellationToken
        ) =
        this.SendResponse(
            HttpMethod.Post,
            "/api/" + endpoint,
            Some body,
            [
                "Origin", origin.GetLeftPart(UriPartial.Authority)
                "X-ClaimCore-Antiforgery", token
                "ClaimCore-Contract", defaultArg fingerprint (ContractIdentity.value)
                "ClaimCore-Installation", defaultArg installation (installationId.ToString("D"))
                match representation with
                | Some value -> Representation.HeaderName, Representation.token value
                | None -> ()
            ],
            ?cancellationToken = cancellationToken
        )

    member this.Call
        (
            endpoint,
            body,
            token,
            ?fingerprint: string,
            ?installation: string,
            ?representation: Representation
        ) =
        task {
            use! response =
                this.CallResponse(
                    endpoint,
                    body,
                    token,
                    ?fingerprint = fingerprint,
                    ?installation = installation,
                    ?representation = representation
                )

            let! payload = response.Content.ReadAsByteArrayAsync()
            return int response.StatusCode, payload
        }

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            application.StopAsync().GetAwaiter().GetResult()
            application.DisposeAsync().AsTask().GetAwaiter().GetResult()
            certificate.Dispose()
            sink.Dispose()
            Directory.Delete(assets, true)

/// Hold a real async authentication owner without starting provider/core work.
let internal holdAuthentication (admission: ClaimCore.Server.AuthenticationAdmission) context =
    task {
        let entered =
            System.Threading.Tasks.TaskCompletionSource(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
            )

        let release =
            System.Threading.Tasks.TaskCompletionSource(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
            )

        let work =
            admission.Run(
                context,
                fun _ ->
                    task {
                        entered.SetResult()
                        do! release.Task
                    }
            )

        do! entered.Task.WaitAsync(System.TimeSpan.FromSeconds 3.)

        return
            { new System.IDisposable with
                member _.Dispose() =
                    release.TrySetResult() |> ignore
                    work.GetAwaiter().GetResult() |> ignore
            }
    }
