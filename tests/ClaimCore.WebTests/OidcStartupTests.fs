module ClaimCore.WebTests.OidcStartupTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open ClaimCore.Contracts

type private SuspendedContent() =
    inherit HttpContent()

    override _.TryComputeLength(length) =
        length <- 0L
        false

    override _.SerializeToStreamAsync(_, _) = Task.Delay(Timeout.Infinite)
    override _.SerializeToStreamAsync(_, _, token) = Task.Delay(Timeout.Infinite, token)

type private DiscoveryHandler(issuer: Uri, suspend: bool) =
    inherit HttpMessageHandler()
    let mutable observed = None
    member _.Observed = observed

    override _.SendAsync(request, _) =
        observed <- Some request.RequestUri
        let response = new HttpResponseMessage(HttpStatusCode.OK)

        response.Content <-
            if suspend then
                new SuspendedContent() :> HttpContent
            else
                new StringContent(
                    $"""{{"issuer":"{issuer.AbsoluteUri}","authorization_endpoint":"https://synthetic.example/auth/","token_endpoint":"https://synthetic.example/token/","jwks_uri":"https://synthetic.example/keys/"}}""",
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
        ServiceClientId = "service"
        CliClientId = "cli"
    }

let private discoveryPaths () =
    for source in
        [
            "https://synthetic.example/"
            "https://synthetic.example/realm/"
            "https://synthetic.example/realm"
        ] do
        let issuer = Uri source
        use handler = new DiscoveryHandler(issuer, false)
        use client = new HttpClient(handler, false)
        OidcStartup.verify client (configuration issuer)

        Expect.equal
            handler.Observed
            (Some(Uri(source.TrimEnd('/') + "/.well-known/openid-configuration")))
            "One joining separator and unchanged issuer identity"

let private suspendedStartup () =
    let issuer = Uri("https://synthetic.example/")
    use handler = new DiscoveryHandler(issuer, true)

    use client =
        new HttpClient(handler, false, Timeout = TimeSpan.FromMilliseconds 100.)

    let work =
        Task.Run(fun () ->
            try
                OidcStartup.verify client (configuration issuer)
                false
            with WebStartupException WebStartupProblem.OidcConfigurationInvalid ->
                true)

    Expect.isTrue (work.Wait(2000)) "Startup does not wait forever on a body after headers"
    Expect.isTrue work.Result "The bounded startup diagnostic contains no provider body"

let tests =
    testList
        "OIDC startup requests"
        [
            testCase
                "[CC-WEB-001] startup requests one exact discovery path for root and path issuers"
                discoveryPaths
            testCase
                "[CC-WEB-001] startup deadline covers a suspended metadata body"
                suspendedStartup
        ]
