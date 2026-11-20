module ClaimCore.ServerTests.AuthFoundationTests

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

open System
open System.IO
open System.Text
open System.Security.Claims
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Expecto
open ClaimCore.Server

let private issuerPolicy () =
    let accepted = OidcAuthority.parse "https://issuer.example.test/realms/claimcore"
    Expect.isOk accepted "A real HTTPS issuer is accepted"

    for rejected in
        [
            "http://issuer.example.test/realms/claimcore"
            "http://127.0.0.1:8080/realms/claimcore"
            "https://issuer.example.test/realms/claimcore?tenant=other"
            "https://user@issuer.example.test/realms/claimcore"
        ] do
        Expect.isError (OidcAuthority.parse rejected) "Production issuer rejects unsafe URI"

let private principalIdentity () =
    let issuer = Uri("https://issuer.example.test/realms/claimcore")
    let human = PrincipalIdentity.human issuer "subject-1"
    let otherHuman = PrincipalIdentity.human issuer "subject-2"
    let service = PrincipalIdentity.service issuer "subject-1"
    Expect.notEqual human otherHuman "Distinct subjects remain distinct"
    Expect.notEqual human service "Human and service identities do not collide"
    Expect.isError (PrincipalIdentity.human issuer " ") "Empty subject is not an actor"
    Expect.isError (PrincipalIdentity.service issuer " ") "Empty client ID is not an actor"

let private metadataValidation () =
    let issuer = Uri("https://issuer.example.test/realms/claimcore")

    let document actualIssuer tokenEndpoint =
        $"""{{"issuer":"{actualIssuer}","authorization_endpoint":"https://issuer.example.test/auth","token_endpoint":"{tokenEndpoint}","jwks_uri":"https://issuer.example.test/keys"}}"""
        |> Encoding.UTF8.GetBytes
        |> ReadOnlyMemory<byte>

    Expect.isOk
        (OidcAuthority.validateMetadata
            issuer
            (document issuer.AbsoluteUri "https://issuer.example.test/token"))
        "Exact issuer and secure metadata endpoints are accepted"

    Expect.isError
        (OidcAuthority.validateMetadata
            issuer
            (document "https://other.example.test" "https://issuer.example.test/token"))
        "Issuer substitution is rejected"

    Expect.isError
        (OidcAuthority.validateMetadata
            issuer
            (document issuer.AbsoluteUri "http://issuer.example.test/token"))
        "Insecure token endpoint is rejected"

let private accessTokenIdentity () =
    let issuer = Uri("https://issuer.example.test/realms/claimcore")

    let principal client subject =
        ClaimsPrincipal(
            ClaimsIdentity([ Claim("azp", client); Claim("sub", subject) ], "verified-bearer")
        )

    let classify client subject =
        PrincipalIdentity.fromAccessToken issuer "claimcore-cli" (principal client subject)

    Expect.equal
        (classify "claimcore-cli" "person-1")
        (PrincipalIdentity.human issuer "person-1")
        "Interactive bearer maps to human subject"

    Expect.equal
        (classify "claimcore-service" "service-account")
        (PrincipalIdentity.service issuer "claimcore-service")
        "Service bearer maps to client ID, not service-account subject"

    Expect.equal
        (classify "another-service" "service-subject")
        (PrincipalIdentity.service issuer "another-service")
        "Each other verified client is a distinct service"

let private middlewareConfiguration () =
    {
        Issuer = Uri("https://issuer.example.test/realms/claimcore")
        TrustRoot = None
        ClientId = "claimcore-web"
        ClientSecret = "synthetic-secret"
        ApiAudience = "claimcore-api"
        CliClientId = "claimcore-cli"
    }

let private middlewarePolicy () =
    let configuration = middlewareConfiguration ()

    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddDataProtection() |> ignore

    AuthMiddleware.configure configuration (TimeSpan.FromHours(8.)) services

    use provider = services.BuildServiceProvider()

    let bearer =
        provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(AuthMiddleware.BearerScheme)

    let oidc =
        provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme)

    Expect.isTrue bearer.RequireHttpsMetadata "Bearer discovery is HTTPS-only"
    Expect.equal bearer.Audience "claimcore-api" "Access token audience is distinct"

    Expect.equal
        (bearer.TokenValidationParameters.ValidAlgorithms |> Seq.toList)
        [ "RS256"; "PS256" ]
        "Only reviewed signing algorithms are accepted"

    Expect.equal
        (bearer.TokenValidationParameters.ValidTypes |> Seq.toList)
        [ "JWT"; "at+jwt" ]
        "Bearer token header type is allowlisted"

    Expect.isTrue oidc.RequireHttpsMetadata "BFF discovery is HTTPS-only"
    Expect.isTrue oidc.UsePkce "BFF uses PKCE"
    Expect.isFalse oidc.SaveTokens "BFF does not save tokens in the cookie ticket"

    for handler in [ bearer.BackchannelHttpHandler; oidc.BackchannelHttpHandler ] do
        let https =
            handler
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "An issuer backchannel is required")
            :?> HttpClientHandler

        Expect.isTrue https.CheckCertificateRevocationList "Issuer key fetches check revocation"
        Expect.isFalse https.AllowAutoRedirect "Issuer backchannels do not follow redirects"

    Expect.throws
        (fun () ->
            AuthMiddleware.configure
                { configuration with
                    Issuer = Uri("http://127.0.0.1:8080/realms/synthetic")
                }
                (TimeSpan.FromHours(8.))
                (ServiceCollection()))
        "Production middleware refuses a loopback HTTP issuer"

let private pushedLanguage () =
    task {
        let configuration = middlewareConfiguration ()
        let authorization = configuration.Issuer.AbsoluteUri + "/auth"
        let pushed = configuration.Issuer.AbsoluteUri + "/par"
        let reference = "urn:ietf:params:oauth:request_uri:synthetic"
        let observed = ResizeArray<string option>()

        use backchannel =
            { new HttpMessageHandler() with
                override _.SendAsync(request, cancellation) =
                    task {
                        Expect.equal
                            request.Method
                            HttpMethod.Post
                            "Authorization is pushed by POST"

                        Expect.equal
                            (nonNull request.RequestUri).AbsoluteUri
                            pushed
                            "The configured PAR endpoint"

                        let! body = (nonNull request.Content).ReadAsStringAsync cancellation
                        let fields = QueryHelpers.ParseQuery body

                        // Retain only the allowlisted language observation. PAR also carries private parameters.
                        match fields.TryGetValue "ui_locales" with
                        | false, _ -> observed.Add None
                        | true, value when value.Count = 1 && value.ToString() = "ar" ->
                            observed.Add(Some "ar")
                        | _ -> failtest "The pushed language was not the single expected value."

                        return
                            new HttpResponseMessage(
                                HttpStatusCode.Created,
                                Content =
                                    new StringContent(
                                        $"""{{"request_uri":"{reference}","expires_in":60}}""",
                                        Encoding.UTF8,
                                        "application/json"
                                    )
                            )
                    }
            }

        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        let keys = Directory.CreateTempSubdirectory("claimcore-par-keys-")

        use _cleanup =
            { new IDisposable with
                member _.Dispose() = keys.Delete true
            }

        builder.Services.AddDataProtection().PersistKeysToFileSystem(keys) |> ignore

        AuthMiddleware.configure configuration (TimeSpan.FromHours 8.) builder.Services

        builder.Services.Configure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            fun (options: OpenIdConnectOptions) ->
                options.Configuration <-
                    OpenIdConnectConfiguration(
                        Issuer = configuration.Issuer.AbsoluteUri,
                        AuthorizationEndpoint = authorization,
                        PushedAuthorizationRequestEndpoint = pushed
                    )

                options.BackchannelHttpHandler <- backchannel
        )
        |> ignore

        use application = builder.Build()
        application.UseAuthentication() |> ignore

        application.MapGet(
            "/challenge",
            Func<HttpContext, Task>(fun context ->
                let properties = AuthenticationProperties()

                if context.Request.Query.ContainsKey "arabic" then
                    properties.Items["ui_locales"] <- "ar"

                context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, properties))
        )
        |> ignore

        do! application.StartAsync()
        use browser = application.GetTestClient()
        browser.BaseAddress <- Uri "https://claimcore.example.test"

        for path in [ "/challenge"; "/challenge?arabic" ] do
            use! response = browser.GetAsync path

            Expect.equal
                response.StatusCode
                HttpStatusCode.Redirect
                "The browser receives a redirect"

            let query = QueryHelpers.ParseQuery((nonNull response.Headers.Location).Query)

            Expect.isFalse
                (query.ContainsKey "ui_locales")
                "The browser hop only carries the request reference"

            Expect.isTrue
                (query.ContainsKey "request_uri" && query["request_uri"].ToString() = reference)
                "The browser follows the synthetic pushed request reference"

        Expect.sequenceEqual
            observed
            [ None; Some "ar" ]
            "The backchannel omits absent language and carries Arabic exactly once"
    }

let private exclusiveCredentialMode () =
    let context = DefaultHttpContext()
    Expect.equal (RequestGate.credentialMode context) CredentialMode.Missing "No credential"

    context.Request.Headers["Authorization"] <- "Bearer synthetic"
    Expect.equal (RequestGate.credentialMode context) CredentialMode.Bearer "Bearer only"

    context.Request.Headers["Cookie"] <- "__Host-ClaimCoreSession=synthetic"
    Expect.equal (RequestGate.credentialMode context) CredentialMode.Mixed "No mixed mode"

    context.Request.Headers.Remove("Authorization") |> ignore

    Expect.equal
        (RequestGate.credentialMode context)
        CredentialMode.BrowserCookie
        "Server-side browser cookie only"

[<Tests>]
let tests =
    testList
        "OIDC authentication foundation"
        [
            testCase "production issuer requires HTTPS and exact URI" issuerPolicy
            testCase "human and service principals remain distinct" principalIdentity
            testCase "metadata pins issuer and secure endpoints" metadataValidation
            testCase "bearer principal uses explicit client classification" accessTokenIdentity
            testCase "middleware pins OIDC and JWT validation policy" middlewarePolicy
            testCaseTask "PAR sends the checked language on the private backchannel" pushedLanguage
            testCase "browser and bearer credentials never mix" exclusiveCredentialMode
        ]
