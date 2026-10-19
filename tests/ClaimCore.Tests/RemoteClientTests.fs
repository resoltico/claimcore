module ClaimCore.Tests.RemoteClientTests

open ClaimCore.Wire

open System
open System.Security.Cryptography
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Duende.IdentityModel.Client
open Duende.IdentityModel.OidcClient
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Application

let private validConfiguration () =
    match
        RemoteConfiguration.create
            "https://claims.example.test/"
            "https://identity.example.test/realms/synthetic"
            "claimcore-cli"
            RemoteClientMode.Interactive
            None
    with
    | Ok value ->
        Expect.equal value.Service.Host "claims.example.test" "Exact HTTPS service"
        Expect.equal value.Mode RemoteClientMode.Interactive "Interactive principal"
    | Error _ -> failtest "Expected valid client configuration"

let private failClosedConfiguration () =
    for service in
        [
            "http://claims.example.test/"
            "https://user:password@claims.example.test/"
            "https://claims.example.test/?token=private"
            "https://claims.example.test/#fragment"
            "https://claims.example.test/api/"
        ] do
        let result =
            RemoteConfiguration.create
                service
                "https://identity.example.test/realms/synthetic"
                "claimcore-cli"
                RemoteClientMode.Interactive
                None

        Expect.equal result (Error ClientFailure.Configuration) "Unsafe service origin refused"

    let result =
        RemoteConfiguration.create
            "https://claims.example.test/"
            "https://identity.example.test/realms/synthetic"
            "claimcore-automation"
            RemoteClientMode.Automation
            None

    Expect.equal
        result
        (Error ClientFailure.Configuration)
        "Automation cannot proceed without private secret file"

let private issuer = Uri "https://identity.example.test/realms/synthetic"

let private metadata =
    """{"issuer":"https://identity.example.test/realms/synthetic","authorization_endpoint":"https://identity.example.test/realms/synthetic/authorize","token_endpoint":"https://identity.example.test/realms/synthetic/token","jwks_uri":"https://identity.example.test/realms/synthetic/keys"}"""

let private keys =
    """{"keys":[{"kty":"RSA","n":"AQAB","e":"AQAB","kid":"synthetic"}]}"""

let private provider document =
    { new HttpMessageHandler() with
        override _.SendAsync(request, _) =
            let reply = new HttpResponseMessage(HttpStatusCode.OK)

            let json =
                if
                    (request.RequestUri |> nonNull)
                        .AbsolutePath.EndsWith("/keys", StringComparison.Ordinal)
                then
                    keys
                else
                    document

            reply.Content <- new StringContent(json, Encoding.UTF8, "application/json")
            Task.FromResult reply
    }

let private discovery () =
    let documents =
        [
            metadata, true
            metadata.Replace(
                "\"issuer\":\"https://identity.example.test",
                "\"issuer\":\"https://evil.example.test"
            ),
            false
            metadata.Replace(
                "https://identity.example.test/realms/synthetic/token",
                "http://identity.example.test/realms/synthetic/token"
            ),
            false
            metadata.Replace(
                "https://identity.example.test/realms/synthetic/token",
                "https://evil.example.test/realms/synthetic/token"
            ),
            false
            "{", false
        ]

    for document, valid in documents do
        use handler = provider document
        use client = new HttpClient(handler, false)
        client.MaxResponseContentBufferSize <- 65536L

        let result =
            OidcClient.discover client issuer CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Expect.equal (Result.isOk result) valid "Default maintained discovery policy"

let private libraryConfiguration () =
    use browser = new LoopbackBrowser()
    let options = InteractiveAuthorization.configuration issuer "claimcore-cli" browser
    Expect.isFalse options.LoadProfile "No userinfo request"
    Expect.isTrue (isNull options.IdentityTokenValidator) "No identity-token validator is installed"
    Expect.equal options.Scope "openid" "Only the protocol scope"
    Expect.isTrue options.Policy.Discovery.RequireHttps "HTTPS discovery default retained"
    Expect.isTrue options.Policy.Discovery.ValidateIssuerName "Exact issuer policy retained"
    Expect.isTrue options.Policy.Discovery.ValidateEndpoints "Endpoint policy retained"

    Expect.isTrue
        (options.RedirectUri.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
        "Only loopback callback is HTTP"

let private libraryPkce () =
    use browser = new LoopbackBrowser()
    use handler = provider metadata
    use client = new HttpClient(handler, false)
    let options = InteractiveAuthorization.configuration issuer "claimcore-cli" browser
    options.BackchannelHandler <- handler
    options.HttpClientFactory <- Func<OidcClientOptions, HttpClient>(fun _ -> client)
    let oidc = Duende.IdentityModel.OidcClient.OidcClient options

    let state =
        oidc.PrepareLoginAsync(cancellationToken = CancellationToken.None).GetAwaiter().GetResult()

    let query = Uri(state.StartUrl).Query

    Expect.isTrue
        (query.Contains("code_challenge_method=S256"))
        "Library authorization requests PKCE S256"

    let challenge =
        SHA256.HashData(Encoding.ASCII.GetBytes state.CodeVerifier)
        |> Convert.ToBase64String
        |> _.TrimEnd('=')
        |> _.Replace('+', '-')
        |> _.Replace('/', '_')

    Expect.isTrue
        (query.Contains("code_challenge=" + challenge))
        "Challenge matches the library verifier"

    let result =
        oidc
            .ProcessResponseAsync(
                browser.RedirectUri + "?code=synthetic&state=forged",
                state,
                cancellationToken = CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

    Expect.isTrue result.IsError "The library rejects a forged callback state"

let private tokenParts () =
    let clock = TimeProvider.System

    let create kind value expiry =
        AccessTokens.fromPartsAt clock (clock.GetUtcNow()) (clock.GetTimestamp()) kind value expiry

    for kind in [ "Bearer"; "bearer"; "BEARER"; "bEaReR" ] do
        match create kind "synthetic-token" 300 with
        | Ok token ->
            Expect.equal (token.ToString()) "<redacted access token>" "Token is not printable"
            Expect.equal token.Authorization.Scheme "Bearer" "Canonical bearer header"
        | Error _ -> failtest "Valid typed token response was refused"

    for kind, value, expiry in
        [
            ("Basic", "synthetic", 300)
            ("Bearer", "", 300)
            ("Bearer", "has space", 300)
            ("Bearer", "has\r\nheader", 300)
            ("Bearer", "é", 300)
            ("Bearer", "synthetic", 0)
        ] do
        Expect.isError (create kind value expiry) "Invalid token metadata never reaches a header"

let private callbackTransport () =
    let host = "127.0.0.1:49152"
    let request = $"GET /?code=synthetic&state=opaque HTTP/1.1\r\nHost: {host}\r\n\r\n"

    Expect.isOk
        (LoopbackTransport.parseRequest host request)
        "Callback query remains opaque for library validation"

    for invalid in
        [
            request.Replace(host, "127.0.0.1:49153")
            request.Replace("GET /?", "GET /other?")
            request.Replace("GET /?", "POST /?")
            request.Replace("Host:", $"Host: {host}\r\nHost:")
        ] do
        Expect.isError
            (LoopbackTransport.parseRequest host invalid)
            "Only one exact loopback host and GET root"

[<Tests>]
let tests =
    testList
        "CLI maintained authentication"
        [
            testCase "HTTPS service configuration" validConfiguration
            testCase
                "unsafe origins and absent automation secret are refused"
                failClosedConfiguration
            testCase "maintained discovery enforces issuer HTTPS and endpoint host" discovery
            testCase
                "library configuration retains access-token-only constraints"
                libraryConfiguration
            testCase "library owns PKCE and callback state validation" libraryPkce
            testCase "typed token data is bounded and redacted" tokenParts
            testCase "loopback transport binds one exact host" callbackTransport
        ]
