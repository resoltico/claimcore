module ClaimCore.WebTests.OidcLiveTests

open System
open System.Net.Http
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.OidcLiveApi
open ClaimCore.WebTests.OidcLiveBrowser

let private wrongRoot issuer =
    task {
        use key = RSA.Create 2048

        let request =
            CertificateRequest(
                "CN=unrelated-root",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            )

        request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

        use root =
            request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1.),
                DateTimeOffset.UtcNow.AddDays 1.
            )

        use client = new HttpClient(AuthMiddleware.syntheticTrustHandler root)

        let! denied =
            task {
                try
                    use! _response = client.GetAsync(issuer + "/.well-known/openid-configuration")
                    return false
                with :? HttpRequestException ->
                    return true
            }

        Expect.isTrue denied "Unrelated issuer CA refused"
    }

let private qualify (server: IdentityServer.Identity) (stage: string -> unit) token =
    task {
        use credentials = JsonDocument.Parse server.Credentials

        let text (name: string) =
            credentials.RootElement.GetProperty(name).GetString() |> nonNull

        let configuration =
            {
                Issuer = Uri server.Issuer
                TrustRoot = Some server.Root
                ClientId = text "webClientId"
                ClientSecret = text "webClientSecret"
                ApiAudience = text "apiAudience"
                ServiceClientId = text "serviceClientId"
                CliClientId = text "publicClientId"
            }

        stage "TLS refusal"
        do! wrongRoot server.Issuer
        use handler = AuthMiddleware.syntheticTrustHandler server.Root
        handler.UseCookies <- true
        use identity = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 30.)
        stage "issuer metadata"
        do! OidcStartup.verify identity configuration token
        use host = new OidcHostFixture.Host(configuration, server.Root)
        stage "PKCE browser"

        let! cookies = login host.Client identity credentials.RootElement stage

        let idToken =
            host.IdToken
            |> Option.defaultWith (fun () -> failtest "ID token was not observed.")

        let! status = bearerRequest host.Client idToken
        Expect.equal status 401 "An ID token is not API access"
        stage "bearer controls"
        do! qualifyBearers server.Issuer identity host.Client credentials.RootElement cookies
        stage "signing-key rotation"
        do! qualifyRotation server.Issuer identity host.Client credentials.RootElement
    }

let private liveIdentity () =
    task {
        let mutable stage = "create owned fixture"
        use! server = Task.FromResult(new IdentityServer.Identity())
        use deadline = new CancellationTokenSource(TimeSpan.FromMinutes 3.)

        try
            stage <- "start owned fixture"
            do! server.Start deadline.Token
            do! qualify server (fun value -> stage <- value) deadline.Token
        with error ->
            match error with
            | CredentialDispatchMismatch(bearer, current, mixed) ->
                printfn
                    "Synthetic credential dispatch statuses: bearer=%d, case=%d, mixed=%d"
                    bearer
                    current
                    mixed
            | InvalidBearerAdmission(index, status) ->
                printfn "Synthetic invalid bearer index %d returned %d" index status
            | _ -> ()

            return
                failtest (
                    "Owned live identity fixture failed at "
                    + stage
                    + "; credentials and token values withheld."
                )
    }

[<Tests>]
let tests =
    testCaseTask
        "[CC-WEB-001] owned Keycloak proves HTTPS, PKCE, bearer refusal controls and key rotation"
        liveIdentity
