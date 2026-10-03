module ClaimCore.Tests.RemoteClientTests

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

        Expect.equal
            result
            (Error ProtocolProblem.ServiceConfigurationInvalid)
            "Unsafe service origin refused"

    let result =
        RemoteConfiguration.create
            "https://claims.example.test/"
            "https://identity.example.test/realms/synthetic"
            "claimcore-automation"
            RemoteClientMode.Automation
            None

    Expect.equal
        result
        (Error ProtocolProblem.ServiceConfigurationInvalid)
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

let private serviceResponseSchema () =
    let model = ContractProjection.current ()

    let endpoint =
        model.WebEndpoints |> List.find (fun item -> item.Identifier = "case.list")

    let schema = WebSchemas.responseDocument model endpoint

    let page = { Items = []; NextCursor = None }

    let bytes = WebWireCodec.list (QueryOutcome.Succeeded page)
    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)

    Expect.isTrue
        (SchemaValueValidation.verify schema document.RootElement)
        "Exact generated service response accepted"

    use extra =
        System.Text.Json.JsonDocument.Parse(
            """{"endpoint":"case.list","outcome":{"tag":"SUCCEEDED","data":{"items":[],"nextCursor":null}},"private":"leak"}"""
        )

    Expect.isFalse
        (SchemaValueValidation.verify schema extra.RootElement)
        "Unexpected response property refused before CLI output"

    let exportId = Guid.Parse("10000000-0000-4000-8000-000000000001")
    let exportBytes = CliServiceRequests.export exportId (String.replicate 64 "a")
    use export = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory exportBytes)

    let exportEndpoint =
        model.WebEndpoints
        |> List.find (fun item -> item.Identifier = "recovery.export")

    match exportEndpoint.Body with
    | Some(JsonBody inputSchema) ->
        let inputDocument =
            { WebSchemas.responseDocument model exportEndpoint with
                Root = inputSchema
            }

        Expect.isTrue
            (SchemaValueValidation.verify inputDocument export.RootElement)
            "Private destination is absent from the exact generated service request"
    | _ -> failtest "Recovery export must have generated JSON input"

let private hostStatusBinding () =
    let bytes = WebWireCodec.hostFailure WebHostFailure.SessionRejected
    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)

    Expect.isTrue
        (RemoteServiceCall.hostStatusMatches 401 document.RootElement)
        "Exact HTTP status matches host body"

    Expect.isFalse
        (RemoteServiceCall.hostStatusMatches 403 document.RootElement)
        "Wrong HTTP status cannot be accepted from a valid body"

let private authorityUncertainty () =
    let eventId = Guid.Parse("10000000-0000-4000-8000-000000000001")

    let bytes =
        WebWireCodec.management "authority.register" (ActorManagementOutcome.Unconfirmed eventId)

    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)
    let reply = CliRemoteWireCodec.result "authority.register" document.RootElement
    Expect.equal reply.ExitCode 4 "Witness-pending authority must not look like definite failure"

let private mutationHostDelivery () =
    let bytes = WebWireCodec.hostFailure WebHostFailure.CompletedResponseFailed
    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)

    for endpoint in (ContractProjection.current ()).CliEndpoints do
        if not endpoint.Cancellable then
            let mutation =
                CliRemoteWireCodec.hostFailure endpoint.Identifier document.RootElement

            Expect.equal mutation.ExitCode 4 $"Lost {endpoint.Identifier} response is unresolved"

    let query = CliRemoteWireCodec.hostFailure "case.list" document.RootElement
    Expect.equal query.ExitCode 3 "Nonmutating query cannot accept authority"

let tests =
    testList
        "authenticated CLI client foundation"
        [
            testCase "HTTPS service client configuration is explicit" validConfiguration
            testCase
                "unsafe origins and missing automation secret fail closed"
                failClosedConfiguration
            testCase "OIDC discovery binds issuer and HTTPS endpoints" oidcMetadata
            testCase "interactive code flow uses S256 PKCE and loopback state" pkce
            testCase "token response parsing is bounded and redacted" tokenResponse
            testCase "PKCE callback requires exact loopback host and state" callback
            testCase "service response validation rejects schema drift" serviceResponseSchema
            testCase "host failure requires matching actual HTTP status" hostStatusBinding
            testCase
                "authority uncertainty retains exit-four recovery direction"
                authorityUncertainty
            testCase "completed mutation response loss remains uncertain" mutationHostDelivery
        ]
