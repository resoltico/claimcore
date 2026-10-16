module ClaimCore.Tests.RemoteClientTests

open ClaimCore.ClientProtocol

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
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

let private oidcMetadata () =
    let issuer = Uri("https://identity.example.test/realms/synthetic")

    let valid =
        """{"issuer":"https://identity.example.test/realms/synthetic","authorization_endpoint":"https://identity.example.test/authorize","token_endpoint":"https://identity.example.test/token","jwks_uri":"https://identity.example.test/keys"}"""

    match OidcClient.parseDiscovery issuer (Encoding.UTF8.GetBytes(valid)) with
    | Ok endpoints -> Expect.equal endpoints.Token.Scheme "https" "HTTPS token endpoint"
    | Error _ -> failtest "Expected valid OIDC metadata"

    for changed in
        [
            valid.Replace(
                "identity.example.test/realms/synthetic",
                "evil.example.test/realms/synthetic"
            )
            valid.Replace(
                "https://identity.example.test/token",
                "http://identity.example.test/token"
            )
            valid.Replace(
                "https://identity.example.test/token",
                "https://identity.example.test/token?leak=1"
            )
            valid.Replace(
                "\"issuer\":",
                "\"issuer\":\"https://identity.example.test/realms/synthetic\",\"issuer\":"
            )
            "{"
        ] do
        Expect.isError
            (OidcClient.parseDiscovery issuer (Encoding.UTF8.GetBytes(changed)))
            "Issuer mismatch, downgrade, and malformed metadata refused"

let private pkce () =
    let session = OidcClient.createPkceSession ()

    let digest =
        SHA256.HashData(Encoding.ASCII.GetBytes(session.Verifier))
        |> Convert.ToBase64String
        |> _.TrimEnd('=')
        |> _.Replace('+', '-')
        |> _.Replace('/', '_')

    Expect.equal session.Challenge digest "S256 challenge is bound to verifier"
    Expect.isTrue (OidcClient.stateMatches session.State session.State) "Exact state"
    Expect.isFalse (OidcClient.stateMatches session.State (session.State + "A")) "Changed state"

    let endpoints =
        {
            Authorization = Uri("https://identity.example.test/authorize")
            Token = Uri("https://identity.example.test/token")
            Jwks = Uri("https://identity.example.test/keys")
        }

    let redirect = Uri("http://127.0.0.1:49152/")

    match OidcClient.authorizationUri endpoints "claimcore-cli" redirect session with
    | Ok uri ->
        Expect.stringContains uri.Query "code_challenge_method=S256" "Mandatory PKCE"
        Expect.stringContains uri.Query "response_type=code" "Authorization code only"

        Expect.stringContains
            uri.Query
            "redirect_uri=http%3A%2F%2F127.0.0.1%3A49152"
            "Native loopback origin uses the provider's any-port registration"
    | Error _ -> failtest "Expected loopback redirect"

    Expect.isError
        (OidcClient.authorizationUri
            endpoints
            "claimcore-cli"
            (Uri("https://remote.example.test/callback"))
            session)
        "Remote redirect refused"

let private tokenResponse () =
    let valid =
        """{"access_token":"synthetic-token","token_type":"Bearer","expires_in":300}"""

    match OAuthTokenClient.parseTokenResponse (Encoding.UTF8.GetBytes(valid)) with
    | Ok token ->
        Expect.equal (token.ToString()) "<redacted access token>" "Secret is not printable"
        Expect.equal token.Authorization.Scheme "Bearer" "Bearer scheme"
    | Error _ -> failtest "Expected bounded synthetic token"

    for invalid in
        [
            valid.Replace("Bearer", "Basic")
            valid.Replace("300", "0")
            valid.Replace("synthetic-token", "")
            "{"
            "[]"
            valid.Replace("\"token_type\":", "\"token_type\":\"Bearer\",\"token_type\":")
        ] do
        Expect.isError
            (OAuthTokenClient.parseTokenResponse (Encoding.UTF8.GetBytes(invalid)))
            "Wrong type, expiry, empty token, and malformed JSON refused"

let private callback () =
    let host = "127.0.0.1:49152"
    let issuer = "https://identity.example.test/realms/synthetic"
    let state = "synthetic-state"

    let query =
        "/?code=synthetic-code&state=synthetic-state&iss="
        + Uri.EscapeDataString(issuer)

    let request = $"GET {query} HTTP/1.1\r\nHost: {host}\r\n\r\n"

    Expect.equal
        (LoopbackCallback.parseRequest host issuer state request)
        (Ok "synthetic-code")
        "Exact loopback callback"

    Expect.isError
        (LoopbackCallback.parseRequest host issuer "forged-state" request)
        "Forged state refused"

    Expect.isError
        (LoopbackCallback.parseRequest "127.0.0.1:49153" issuer state request)
        "Wrong listener host refused"

let private hostileProviderBytes () =
    let issuer = Uri("https://identity.example.test/realms/synthetic")

    for data in
        [
            Unchecked.defaultof<byte array>
            [| 0xffuy |]
            Encoding.UTF8.GetBytes("null")
            Encoding.UTF8.GetBytes("[0]")
            Encoding.UTF8.GetBytes("{\"issuer\":\"\\ud800\"}")
            Array.create 65537 (byte ' ')
        ] do
        Expect.isError
            (OidcClient.parseDiscovery issuer data)
            "Metadata reader is total and bounded"

        Expect.isError
            (OAuthTokenClient.parseTokenResponse data)
            "Token reader is total and bounded"

    for token in [ "has space"; "has\r\nheader"; "\u00e9" ] do
        let json =
            System.Text.Json.JsonSerializer.Serialize(
                {|
                    access_token = token
                    token_type = "Bearer"
                    expires_in = 60
                |}
            )

        Expect.isError
            (OAuthTokenClient.parseTokenResponse (Encoding.UTF8.GetBytes json))
            "Invalid bearer text never reaches an HTTP header"

[<Tests>]
let tests =
    testList
        "CLI authentication"
        [
            testCase
                "hostile provider bytes and bearer header text are refused"
                hostileProviderBytes
            testCase "HTTPS service configuration" validConfiguration
            testCase
                "unsafe origins and absent automation secret are refused"
                failClosedConfiguration
            testCase "metadata pins issuer and TLS endpoints" oidcMetadata
            testCase "authorization uses S256 PKCE and loopback state" pkce
            testCase "tokens are bounded and redacted" tokenResponse
            testCase "callback requires exact host and state" callback
        ]
