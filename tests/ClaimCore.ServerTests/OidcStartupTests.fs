module ClaimCore.ServerTests.OidcStartupTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Server

type private Clock() =
    inherit TimeProvider()
    let mutable elapsed = 0L

    member _.Advance(seconds: int64) =
        elapsed <- elapsed + seconds * TimeSpan.TicksPerSecond

    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    override _.GetTimestamp() = elapsed

type private SuspendedContent() =
    inherit HttpContent()

    override _.TryComputeLength(length) =
        length <- 0L
        false

    override _.SerializeToStreamAsync(_, _) = Task.Delay(Timeout.Infinite)
    override _.SerializeToStreamAsync(_, _, token) = Task.Delay(Timeout.Infinite, token)

type private DiscoveryHandler(issuer: Uri, endSession: bool) =
    inherit HttpMessageHandler()
    let observed = ResizeArray<Uri>()
    let mutable unavailable = false
    let mutable suspend = false

    let key =
        use rsa = RSA.Create(2048)
        let parameters = rsa.ExportParameters(false)

        let encode (value: byte array) =
            System.Buffers.Text.Base64Url.EncodeToString(ReadOnlySpan<byte> value)

        let modulus = encode (nonNull parameters.Modulus)
        let exponent = encode (nonNull parameters.Exponent)

        $"""{{"keys":[{{"kty":"RSA","kid":"synthetic-first","use":"sig","n":"{modulus}","e":"{exponent}"}}]}}"""

    member _.Observed = List.ofSeq observed

    member _.Unavailable
        with set value = unavailable <- value

    member _.Suspend
        with set value = suspend <- value

    override _.SendAsync(request, _) =
        let url = nonNull request.RequestUri
        observed.Add url

        let response =
            new HttpResponseMessage(
                if unavailable then
                    HttpStatusCode.ServiceUnavailable
                else
                    HttpStatusCode.OK
            )

        response.Content <-
            if suspend then
                new SuspendedContent() :> HttpContent
            elif url.AbsolutePath = "/keys/" then
                new StringContent(key, Encoding.UTF8) :> HttpContent
            else
                let logout =
                    if endSession then
                        ""","end_session_endpoint":"https://issuer.example.test/logout" """
                    else
                        ""

                new StringContent(
                    $"""{{"issuer":"{issuer.AbsoluteUri}","authorization_endpoint":"https://synthetic.example/auth/","token_endpoint":"https://synthetic.example/token/","jwks_uri":"https://synthetic.example/keys/"{logout}}}""",
                    Encoding.UTF8
                )
                :> HttpContent

        Task.FromResult(response)

let private configuration issuer =
    {
        Issuer = issuer
        TrustRoot = None
        ClientId = "web"
        ClientSecret = "synthetic"
        ApiAudience = "api"
        CliClientId = "cli"
        ServiceClientIds = Set.singleton "service"
    }

let private discoveryPaths () =
    for source in
        [
            "https://synthetic.example/"
            "https://synthetic.example/realm/"
            "https://synthetic.example/realm"
        ] do
        let authority = Uri source
        use handler = new DiscoveryHandler(authority, true)

        use issuer =
            new IssuerMetadata(configuration authority, new HttpClient(handler, false), Clock())

        let admitted =
            issuer.Get CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> Result.defaultWith (fun failure -> failtestf "Valid trust refused: %A" failure)

        Expect.equal
            admitted.EndSessionEndpoint
            "https://issuer.example.test/logout"
            "Provider endpoint"

        Expect.equal
            handler.Observed
            [
                Uri(source.TrimEnd('/') + "/.well-known/openid-configuration")
                Uri "https://synthetic.example/keys/"
            ]
            "Reads one exact issuer location before its advertised signing keys"

let private outage () =
    task {
        let authority = Uri "https://synthetic.example/"
        let clock = Clock()
        use handler = new DiscoveryHandler(authority, true)
        handler.Unavailable <- true

        use issuer =
            new IssuerMetadata(configuration authority, new HttpClient(handler, false), clock)

        let! cold = issuer.Get CancellationToken.None

        Expect.equal
            (Result.map ignore cold)
            (Error OidcDependencyFailure.Unavailable)
            "Cold 503 is recoverable"

        let! repeat = issuer.Get CancellationToken.None

        Expect.equal
            (Result.map ignore repeat)
            (Error OidcDependencyFailure.Unavailable)
            "Outage retries are bounded"

        Expect.equal handler.Observed.Length 1 "The cooldown coalesces consumers"

        handler.Unavailable <- false
        clock.Advance 3L
        let! ready = issuer.Get CancellationToken.None
        Expect.isOk ready "The same host can resume federation without restarting"

        handler.Unavailable <- true
        clock.Advance 121L
        let! expired = issuer.Get CancellationToken.None

        Expect.equal
            (Result.map ignore expired)
            (Error OidcDependencyFailure.Unavailable)
            "Expired trust cannot authorize new federation"
    }

let private missingLogout () =
    task {
        let authority = Uri "https://synthetic.example/realm"
        use handler = new DiscoveryHandler(authority, false)

        use issuer =
            new IssuerMetadata(configuration authority, new HttpClient(handler, false), Clock())

        let! unavailable = issuer.Get CancellationToken.None

        Expect.equal
            (Result.map ignore unavailable)
            (Error OidcDependencyFailure.Untrusted)
            "Missing required metadata cannot establish trust"

        Expect.equal handler.Observed.Length 1 "Keys are never fetched from rejected metadata"
    }

let private suspendedBody () =
    task {
        let authority = Uri "https://synthetic.example/"
        use handler = new DiscoveryHandler(authority, true)
        handler.Suspend <- true

        use issuer =
            new IssuerMetadata(configuration authority, new HttpClient(handler, false), Clock())

        let! unavailable =
            (issuer.Get CancellationToken.None).WaitAsync(TimeSpan.FromSeconds 8.)

        Expect.equal
            (Result.map ignore unavailable)
            (Error OidcDependencyFailure.Unavailable)
            "The provider deadline covers body reads"
    }

[<Tests>]
let tests =
    testList
        "OIDC issuer evidence"
        [
            testCase "one exact discovery path for root and path issuers" discoveryPaths
            testTask "outage and restoration use the same lifecycle" { do! outage () }
            testTask "missing trust evidence fails closed" { do! missingLogout () }
            testTask "a stalled body reaches a bounded dependency outcome" { do! suspendedBody () }
        ]
