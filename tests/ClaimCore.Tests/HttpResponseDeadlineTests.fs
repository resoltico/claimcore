module ClaimCore.Tests.HttpResponseDeadlineTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli

/// Headers complete, but the content source remains suspended until its reader is cancelled.
type private SuspendedContent() =
    inherit HttpContent()

    override _.TryComputeLength(length) =
        length <- 0L
        false

    override _.SerializeToStreamAsync(_, _) = Task.Delay(Timeout.Infinite)

    override _.SerializeToStreamAsync(_, _, cancellationToken) =
        Task.Delay(Timeout.Infinite, cancellationToken)

type private HeadersOnlyHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_, _) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new SuspendedContent()
        Task.FromResult(response)

let private client () =
    new HttpClient(new HeadersOnlyHandler(), true, Timeout = TimeSpan.FromMilliseconds 100.)

let private discoveryDeadline () =
    use http = client ()

    let work =
        OidcClient.discover http (Uri("https://synthetic.example/")) CancellationToken.None

    Expect.isTrue (work.Wait(2000)) "The complete discovery request is bounded after headers"
    Expect.equal work.Result (Error "OIDC_METADATA_UNAVAILABLE") "No provider body is reflected"

let private tokenDeadline () =
    use http = client ()

    let endpoints =
        {
            Authorization = Uri("https://synthetic.example/auth")
            Token = Uri("https://synthetic.example/token")
            Jwks = Uri("https://synthetic.example/keys")
        }

    let grant =
        OAuthGrant.AuthorizationCode(
            "synthetic-code",
            Uri("http://127.0.0.1:49152/"),
            "synthetic-verifier"
        )

    let work =
        OAuthTokenClient.acquire http endpoints "synthetic-client" grant CancellationToken.None

    Expect.isTrue (work.Wait(2000)) "The complete token request is bounded after headers"

    match work.Result with
    | Error "OIDC_TOKEN_UNAVAILABLE" -> ()
    | _ -> failtest "A suspended token body must refuse without provider details."

let tests =
    testList
        "HTTP response deadlines"
        [
            testCase
                "[CC-CLI-001] discovery deadline covers a suspended body after headers"
                discoveryDeadline
            testCase
                "[CC-CLI-001] token deadline covers a suspended body after headers"
                tokenDeadline
        ]
