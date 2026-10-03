module ClaimCore.WebTests.AuthFoundationTests

open System
open System.Text
open System.Security.Claims
open System.Net.Http
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.TestServerFixture

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
        PrincipalIdentity.fromAccessToken
            issuer
            "claimcore-cli"
            "claimcore-service"
            (principal client subject)

    Expect.equal
        (classify "claimcore-cli" "person-1")
        (PrincipalIdentity.human issuer "person-1")
        "Interactive bearer maps to human subject"

    Expect.equal
        (classify "claimcore-service" "service-account")
        (PrincipalIdentity.service issuer "claimcore-service")
        "Service bearer maps to client ID, not service-account subject"

    Expect.isError (classify "unknown-client" "person-1") "Unknown OAuth client is denied"

let private interimCaseWorkClosed () =
    use host = Host.Start()

    let legacyLogin =
        host.Send(
            HttpMethod.Post,
            "/api/v3/session/login",
            Some "{}",
            Some "application/json",
            None
        )

    Expect.equal legacyLogin.Status 404 "Bootstrap login is absent when OIDC is configured"

    let caseWork =
        host.Send(HttpMethod.Post, "/api/v3/cases/get", Some "{}", Some "application/json", None)

    Expect.equal caseWork.Status 401 "Anonymous case work remains closed"

    let readiness = host.Send(HttpMethod.Get, "/health/ready", None, None, None)
    Expect.equal readiness.Status 503 "The interim OIDC host is not ready"

    Expect.equal
        readiness.DataUseScope
        (Some "SYNTHETIC_ONLY")
        "Preview scope is visible without case data"

    Expect.equal readiness.DataUsePhase (Some "ACTIVE") "Preview phase is explicit"

    Expect.equal
        readiness.RealDataReady
        (Some "false")
        "Generic source preview cannot claim real-data readiness"

let private opaqueSessionStore () =
    let principal =
        ClaimsPrincipal(ClaimsIdentity([ Claim("sub", "synthetic-human") ], "oidc"))

    let properties = AuthenticationProperties()
    properties.ExpiresUtc <- Nullable(DateTimeOffset.UtcNow.AddMinutes(5.))

    let ticket =
        AuthenticationTicket(
            principal,
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme
        )

    let store =
        OidcTicketStore(TimeSpan.FromMinutes(30.), TimeSpan.FromHours(8.)) :> ITicketStore

    let key = store.StoreAsync(ticket).GetAwaiter().GetResult()
    Expect.equal key.Length 32 "The cookie holds only a random lookup key"
    Expect.isNotNull (store.RetrieveAsync(key).GetAwaiter().GetResult()) "Ticket stays server-side"
    store.RemoveAsync(key).GetAwaiter().GetResult()
    Expect.isNull (store.RetrieveAsync(key).GetAwaiter().GetResult()) "Logout revokes the ticket"

    let expired = AuthenticationProperties()
    expired.ExpiresUtc <- Nullable(DateTimeOffset.UtcNow.AddMinutes(-1.))

    let stale =
        AuthenticationTicket(principal, expired, CookieAuthenticationDefaults.AuthenticationScheme)

    let staleKey = store.StoreAsync(stale).GetAwaiter().GetResult()

    Expect.isNull
        (store.RetrieveAsync(staleKey).GetAwaiter().GetResult())
        "Expired tickets are swept"

let private middlewareConfiguration () =
    {
        Issuer = Uri("https://issuer.example.test/realms/claimcore")
        TrustRoot = None
        ClientId = "claimcore-web"
        ClientSecret = "synthetic-secret"
        ApiAudience = "claimcore-api"
        ServiceClientId = "claimcore-service"
        CliClientId = "claimcore-cli"
    }

let private middlewarePolicy () =
    let configuration = middlewareConfiguration ()

    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddDataProtection() |> ignore

    AuthMiddleware.configure
        configuration
        (TimeSpan.FromMinutes(30.))
        (TimeSpan.FromHours(8.))
        services

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
        [ "JWT"; "at+jwt"; "Bearer" ]
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
                (TimeSpan.FromMinutes(30.))
                (TimeSpan.FromHours(8.))
                (ServiceCollection()))
        "Production middleware refuses a loopback HTTP issuer"

let private exclusiveCredentialMode () =
    let context = DefaultHttpContext()
    Expect.equal (Admission.credentialMode context) CredentialMode.Missing "No credential"

    context.Request.Headers["Authorization"] <- "Bearer synthetic"
    Expect.equal (Admission.credentialMode context) CredentialMode.Bearer "Bearer only"

    context.Request.Headers["Cookie"] <- "__Host-ClaimCoreSession=synthetic"
    Expect.equal (Admission.credentialMode context) CredentialMode.Mixed "No mixed mode"

    context.Request.Headers.Remove("Authorization") |> ignore

    Expect.equal
        (Admission.credentialMode context)
        CredentialMode.BrowserCookie
        "Server-side browser cookie only"

let tests =
    testList
        "OIDC authentication foundation"
        [
            testCase "[CC-WEB-001] production issuer requires HTTPS and exact URI" issuerPolicy
            testCase "[CC-WEB-001] human and service principals remain distinct" principalIdentity
            testCase "[CC-WEB-001] metadata pins issuer and secure endpoints" metadataValidation
            testCase
                "[CC-WEB-001] bearer principal uses explicit client classification"
                accessTokenIdentity
            testCase
                "[CC-WEB-001] OIDC mode closes legacy case work until grants exist"
                interimCaseWorkClosed
            testCase "[CC-WEB-001] OIDC cookie uses revocable server-side ticket" opaqueSessionStore
            testCase "[CC-WEB-001] middleware pins OIDC and JWT validation policy" middlewarePolicy
            testCase "[CC-WEB-001] browser and bearer credentials never mix" exclusiveCredentialMode
        ]
