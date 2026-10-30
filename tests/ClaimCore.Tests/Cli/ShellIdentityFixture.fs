module ClaimCore.Tests.ShellIdentityFixture

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open Expecto

open ShellTestClock

let private pairs (value: string) =
    let decode (part: string) =
        Uri.UnescapeDataString(part.Replace("+", " "))

    value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun part ->
        match part.Split('=', 2) with
        | [| name; value |] -> decode name, decode value
        | _ -> failtest "Expected named OAuth parameter.")
    |> Map.ofArray

let private response status source =
    let value = new HttpResponseMessage(status)
    value.Content <- new StringContent(source, Encoding.UTF8, "application/json")
    value

let private challenge (verifier: string) =
    SHA256.HashData(Encoding.ASCII.GetBytes verifier)
    |> Convert.ToBase64String
    |> _.TrimEnd('=')
    |> _.Replace('+', '-')
    |> _.Replace('/', '_')

/// Exercises real maintained discovery/code/PKCE processing; only HTTPS transport and browser UI are injected.
type Provider(clock: Clock) =
    inherit HttpMessageHandler()
    let issuer = "https://identity.example.test/realm"
    let callbacks = ResizeArray<Task>()
    let mutable signIns = 0
    let mutable refreshes = 0
    let mutable expectedChallenge = ""
    let mutable expectedCode = ""
    let mutable expectedRefresh = ""
    let mutable failRefresh = false
    let mutable rotateRefresh = true

    let tokens rotate =
        let access = "synthetic-access-" + string signIns + "-" + string refreshes
        let refresh = "synthetic-refresh-" + string signIns + "-" + string refreshes

        if rotate then
            expectedRefresh <- refresh

        if rotate then
            JsonSerializer.Serialize
                {|
                    access_token = access
                    token_type = "Bearer"
                    expires_in = 60
                    refresh_token = refresh
                |}
        else
            JsonSerializer.Serialize
                {|
                    access_token = access
                    token_type = "Bearer"
                    expires_in = 60
                |}

    let exchange (request: HttpRequestMessage) (cancelled: CancellationToken) =
        task {
            let! source = (request.Content |> nonNull).ReadAsStringAsync cancelled
            let form = pairs source
            Expect.equal form["client_id"] "synthetic-cli" "Public client binding"
            Expect.isFalse (Map.containsKey "client_secret" form) "No public-client secret"

            match form["grant_type"] with
            | "authorization_code" ->
                Expect.equal form["code"] expectedCode "Only the issued authorization code"

                Expect.equal
                    (challenge form["code_verifier"])
                    expectedChallenge
                    "Actual code exchange proves S256 verifier"

                return response HttpStatusCode.OK (tokens true)
            | "refresh_token" ->
                refreshes <- refreshes + 1
                Expect.equal form["refresh_token"] expectedRefresh "Latest refresh token is used"

                if failRefresh then
                    failRefresh <- false
                    return response HttpStatusCode.BadRequest "{\"error\":\"invalid_grant\"}"
                else
                    return response HttpStatusCode.OK (tokens rotateRefresh)
            | _ -> return failtest "Unexpected OAuth grant."
        }

    member _.Clock = clock
    member _.Issuer = Uri issuer
    member _.SignIns = signIns
    member _.Refreshes = refreshes
    member _.FailRefresh() = failRefresh <- true
    member _.RotateRefresh(value) = rotateRefresh <- value

    member _.Launch(address: string) =
        let query = pairs (Uri address).Query
        signIns <- signIns + 1
        Expect.equal query["code_challenge_method"] "S256" "Actual authorization uses PKCE"
        expectedChallenge <- query["code_challenge"]
        expectedCode <- "synthetic-code-" + string signIns
        let redirect = Uri query["redirect_uri"]
        Expect.equal redirect.Host "127.0.0.1" "Browser callback stays IPv4 loopback"
        Expect.equal redirect.Scheme "http" "Only the native callback uses HTTP"

        let callback =
            redirect.AbsoluteUri
            + "?code="
            + expectedCode
            + "&state="
            + Uri.EscapeDataString query["state"]

        let sending =
            task {
                use handler = new HttpClientHandler(UseProxy = false)
                use client = new HttpClient(handler)
                client.Timeout <- TimeSpan.FromSeconds 5.
                use! reply = client.GetAsync callback

                Expect.equal
                    reply.StatusCode
                    HttpStatusCode.OK
                    "Real callback listener admits library state"
            }

        callbacks.Add sending
        Ok()

    member _.Completed() = Task.WhenAll callbacks

    override _.SendAsync(request, cancelled) =
        let path = (request.RequestUri |> nonNull).AbsolutePath

        if path.EndsWith("/token", StringComparison.Ordinal) then
            exchange request cancelled
        else
            let source =
                if path.EndsWith("/keys", StringComparison.Ordinal) then
                    "{\"keys\":[{\"kty\":\"RSA\",\"n\":\"AQAB\",\"e\":\"AQAB\",\"kid\":\"synthetic\"}]}"
                else
                    JsonSerializer.Serialize
                        {|
                            issuer = issuer
                            authorization_endpoint = issuer + "/authorize"
                            token_endpoint = issuer + "/token"
                            jwks_uri = issuer + "/keys"
                        |}

            Task.FromResult(response HttpStatusCode.OK source)

let internal session (provider: Provider) =
    new MemoryTokenSession(
        InteractiveAuthorization.obtainWith
            {
                Clock = provider.Clock
                Launch = provider.Launch
            }
            provider
            provider.Issuer
            "synthetic-cli",
        OAuthRefresh.run provider.Clock provider "synthetic-cli"
    )

let internal token (session: MemoryTokenSession) =
    session.Token CancellationToken.None
    |> _.GetAwaiter().GetResult()
    |> Result.defaultWith (fun _ -> failtest "Expected admitted in-memory token.")
