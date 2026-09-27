module ClaimCore.WebTests.OidcLiveFixture

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Web
open ClaimCore.WebTests.OidcLiveApi
open ClaimCore.WebTests.OidcLiveBrowser
open ClaimCore.WebTests.RouteFixtures

let private requiredEnvironment name =
    Environment.GetEnvironmentVariable(name)
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultWith (fun () -> invalidOp "Synthetic OIDC fixture environment is missing.")

let private value (root: JsonElement) (name: string) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Synthetic OIDC credential field is missing.")

let private syntheticConfiguration issuer (root: X509Certificate2) (credentials: JsonElement) =
    {
        Issuer = Uri issuer
        TrustRoot = Some root
        ClientId = value credentials "webClientId"
        ClientSecret = value credentials "webClientSecret"
        ApiAudience = value credentials "apiAudience"
        ServiceClientId = value credentials "serviceClientId"
        CliClientId = value credentials "publicClientId"
    }

let private webConfiguration oidc (root: X509Certificate2) =
    {
        Origin = Uri("https://localhost:5443")
        ConnectionString = "synthetic-not-opened"
        WitnessConnectionString = "synthetic-not-opened"
        WitnessKeyRingPath = "synthetic-not-opened"
        SuppressionKeyPath = "synthetic-not-opened"
        RecoveryArtifactKeyPath = "synthetic-not-opened"
        StateDirectory = "synthetic-not-opened"
        Certificate = root
        SessionIdle = TimeSpan.FromMinutes(30.)
        SessionAbsolute = TimeSpan.FromHours(8.)
        Admission =
            {
                MaximumJsonBytes = 65536
                CorePermitLimit = 4
                CoreQueueLimit = 1
                LoginPermitLimit = 1
            }
        Oidc = Some oidc
    }

let private rejectOtherCa issuer =
    task {
        use key = RSA.Create(2048)

        let request =
            CertificateRequest(
                "CN=other-synthetic-ca",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            )

        request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

        use other =
            request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1.),
                DateTimeOffset.UtcNow.AddDays(1.)
            )

        use client = new HttpClient(AuthMiddleware.syntheticTrustHandler other)
        let mutable refused = false

        try
            use! _response = client.GetAsync(issuer + "/.well-known/openid-configuration")
            ()
        with :? HttpRequestException ->
            refused <- true

        if not refused then
            invalidOp "An unrelated CA was trusted for the synthetic issuer."
    }

let private configureHostServices
    configuration
    root
    (idToken: string option ref)
    (services: IServiceCollection)
    =
    services.AddDataProtection().UseEphemeralDataProtectionProvider() |> ignore

    AuthMiddleware.configureSyntheticLoopback
        configuration
        root
        (fun token -> idToken.Value <- Some token)
        (TimeSpan.FromMinutes(30.))
        (TimeSpan.FromHours(8.))
        services

    services.AddAuthorization() |> ignore

    services.AddAntiforgery(fun options -> options.HeaderName <- "X-ClaimCore-Antiforgery")
    |> ignore

    services.AddRateLimiter(fun options ->
        options.AddConcurrencyLimiter(
            "core",
            fun limiter ->
                limiter.PermitLimit <- 4
                limiter.QueueLimit <- 0
        )
        |> ignore)
    |> ignore

let private startHost configuration (root: X509Certificate2) =
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseTestServer() |> ignore
    let idToken = ref None
    configureHostServices configuration root idToken builder.Services

    let application = builder.Build()

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            context.Connection.RemoteIpAddress <- IPAddress.Loopback
            next.Invoke(context))
    )
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore

    ApiRoutes.map
        (webConfiguration configuration root)
        (fun _ -> RuntimeStub().ActorCore)
        application

    application.MapGet(
        "/auth/login",
        Func<HttpContext, Task>(fun context ->
            context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme))
    )
    |> ignore

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

    application.StartAsync().GetAwaiter().GetResult()
    let local = application.GetTestClient()
    local.BaseAddress <- Uri("https://localhost:5443")
    application, local, idToken

let run () =
    let mutable stage = "configuration"

    try
        task {
            let issuer = requiredEnvironment "CLAIMCORE_TEST_OIDC_ISSUER"
            let credentialsPath = requiredEnvironment "CLAIMCORE_TEST_OIDC_CREDENTIALS"
            let caPath = requiredEnvironment "CLAIMCORE_TEST_OIDC_CA_CERT"
            use credentials = JsonDocument.Parse(File.ReadAllBytes(credentialsPath))
            use root = X509CertificateLoader.LoadCertificateFromFile(caPath)
            let configuration = syntheticConfiguration issuer root credentials.RootElement
            stage <- "wrong-ca"
            do! rejectOtherCa issuer

            if
                ClaimCore.Application.PrincipalKey.human issuer "synthetic-owner"
                |> Result.isError
            then
                invalidOp "Synthetic HTTPS issuer was refused by Application identity policy."

            stage <- "host"
            let application, local, idToken = startHost configuration root
            use application = application
            use local = local
            use handler = AuthMiddleware.syntheticTrustHandler root
            handler.AllowAutoRedirect <- false
            handler.UseCookies <- true
            use identity = new HttpClient(handler)
            stage <- "browser"

            let! browserCookies =
                login local identity credentials.RootElement (fun value -> stage <- value)

            stage <- "id-token-refusal"

            let issuedIdToken =
                idToken.Value
                |> Option.defaultWith (fun () ->
                    invalidOp "Synthetic BFF ID token was not observed.")

            let! idTokenStatus = bearerRequest local issuedIdToken

            if idTokenStatus <> 401 then
                invalidOp "Synthetic ID token was accepted as API access."

            stage <- "bearer"
            do! qualifyBearers issuer identity local credentials.RootElement browserCookies
            stage <- "key-rotation"
            do! qualifyRotation issuer identity local credentials.RootElement
            do! application.StopAsync()

            printfn
                "Synthetic Web OIDC PKCE, case API credential dispatch, JWT controls, and key rotation passed."
        }
        |> _.GetAwaiter().GetResult()
    with _ ->
        eprintfn "Synthetic Web OIDC stage failed: %s." stage
        reraise ()
