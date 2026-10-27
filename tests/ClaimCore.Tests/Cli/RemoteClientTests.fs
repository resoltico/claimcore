module ClaimCore.Tests.RemoteClientTests

open ClaimCore.Protocol
open ClaimCore.Domain

open System
open System.Security.Cryptography
open System.Net
open System.Net.Http
open System.Net.Sockets
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

let private withListener action =
    task {
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.)
        let endpoint = listener.LocalEndpoint :?> IPEndPoint

        try
            return! action listener endpoint deadline.Token
        finally
            listener.Stop()
    }

let private withCallback action =
    withListener (fun listener endpoint cancelled ->
        action
            endpoint
            (LoopbackTransport.readCallback listener (string endpoint) cancelled)
            cancelled)

let private sendCallback (endpoint: IPEndPoint) source cancelled =
    task {
        use client = new TcpClient()
        do! client.ConnectAsync(endpoint.Address, endpoint.Port, cancelled)
        use stream = client.GetStream()
        let bytes = Encoding.ASCII.GetBytes(source: string)
        do! stream.WriteAsync(bytes.AsMemory(), cancelled)
        client.Client.Shutdown SocketShutdown.Send
        use reply = new System.IO.MemoryStream()
        do! stream.CopyToAsync(reply, cancelled)
        return Encoding.ASCII.GetString(reply.ToArray())
    }

let private callbackRequest endpoint =
    $"GET /?code=synthetic&state=opaque HTTP/1.1\r\nHost: {endpoint}\r\n\r\n"

let private unusedConnections idle =
    withCallback (fun endpoint pending cancelled ->
        task {
            use unused = new TcpClient()
            do! unused.ConnectAsync(endpoint.Address, endpoint.Port, cancelled)

            if not idle then
                unused.Dispose()

            let sending = sendCallback endpoint (callbackRequest endpoint) cancelled
            let! result = pending
            let! response = sending
            Expect.isOk result "An unused connection does not consume the one callback"
            Expect.isTrue (response.StartsWith("HTTP/1.1 200 OK")) "Actual callback succeeds"
        })

let private malformedConnection partial =
    withCallback (fun endpoint pending cancelled ->
        task {
            let request =
                if partial then
                    "GET / HTTP/1.1\r\n"
                else
                    "POST / HTTP/1.1\r\n\r\n"

            let sending = sendCallback endpoint request cancelled
            let! result = pending
            let! response = sending
            Expect.isError result "An attempted malformed callback remains a refusal"
            Expect.isTrue (response.StartsWith("HTTP/1.1 400 Bad Request")) "Refusal response"
        })

let private stalledPartialConnection () =
    withCallback (fun endpoint pending cancelled ->
        task {
            use client = new TcpClient()
            do! client.ConnectAsync(endpoint.Address, endpoint.Port, cancelled)
            use stream = client.GetStream()
            do! stream.WriteAsync(ReadOnlyMemory [| byte 'G' |], cancelled)
            let! result = pending
            Expect.isError result "A partial header timeout consumes and refuses the callback"
            use response = new System.IO.MemoryStream()
            do! stream.CopyToAsync(response, cancelled)

            Expect.isTrue
                (Encoding.ASCII
                    .GetString(response.ToArray())
                    .StartsWith("HTTP/1.1 400 Bad Request"))
                "A stalled partial request receives the refusal before its socket closes"
        })

type private ObservedNetworkStream(socket, received: TaskCompletionSource<unit>) =
    inherit NetworkStream(socket, false)

    override _.ReadAsync(buffer, cancelled) =
        let pending = base.ReadAsync(buffer, cancelled)

        ValueTask<int>(
            task {
                let! count = pending

                if count > 0 then
                    received.TrySetResult() |> ignore

                return count
            }
        )

let private resetHeaders partial (listener: TcpListener) (endpoint: IPEndPoint) cancelled =
    task {
        use client = new TcpClient()
        do! client.ConnectAsync(endpoint.Address, endpoint.Port, cancelled)
        use! accepted = listener.AcceptTcpClientAsync(cancelled)

        let received =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        use stream = new ObservedNetworkStream(accepted.Client, received)
        let pending = LoopbackTransport.readHeaders stream cancelled

        if partial then
            do! client.GetStream().WriteAsync(ReadOnlyMemory [| byte 'G' |], cancelled)
            do! received.Task.WaitAsync(cancelled)

        client.LingerState <- LingerOption(true, 0)
        client.Dispose()

        try
            let! result = pending

            if partial then
                Expect.isError result "A reset after received header bytes cannot be ignored"
            else
                Expect.equal result (Ok None) "An unused reset socket has no callback"
        with :? System.IO.IOException when partial ->
            ()
    }

let private cancelledPartialConnection () =
    withListener (fun listener endpoint cancelled ->
        task {
            use client = new TcpClient()
            do! client.ConnectAsync(endpoint.Address, endpoint.Port, cancelled)
            use! accepted = listener.AcceptTcpClientAsync(cancelled)
            use cancellation = CancellationTokenSource.CreateLinkedTokenSource cancelled

            let received =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            use stream = new ObservedNetworkStream(accepted.Client, received)
            let pending = LoopbackTransport.readConnection stream cancellation.Token
            do! client.GetStream().WriteAsync(ReadOnlyMemory [| byte 'G' |], cancelled)
            do! received.Task.WaitAsync(cancelled)
            cancellation.Cancel()

            try
                let! _ = pending
                failtest "Outer cancellation after a header byte remains cancellation"
            with :? OperationCanceledException ->
                ()
        })

let private resetConnection partial =
    withListener (fun listener endpoint cancelled ->
        task {
            do! resetHeaders partial listener endpoint cancelled

            if not partial then
                let pending = LoopbackTransport.readCallback listener (string endpoint) cancelled
                let sending = sendCallback endpoint (callbackRequest endpoint) cancelled
                let! result = pending
                let! response = sending
                Expect.isOk result "The actual callback follows an unused reset connection"
                Expect.isTrue (response.StartsWith("HTTP/1.1 200 OK")) "Actual callback succeeds"
        })

let private cancelledCallback idle =
    task {
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        use deadline = new CancellationTokenSource()
        use client = new TcpClient()

        try
            if idle then
                let endpoint = listener.LocalEndpoint :?> IPEndPoint
                do! client.ConnectAsync(endpoint.Address, endpoint.Port)

            let watch = System.Diagnostics.Stopwatch.StartNew()
            let pending = LoopbackTransport.readCallback listener "127.0.0.1" deadline.Token
            deadline.CancelAfter(TimeSpan.FromMilliseconds 100.)

            try
                let! _ = pending
                failtest "Overall cancellation must terminate callback admission"
            with :? OperationCanceledException ->
                ()

            Expect.isLessThan
                watch.Elapsed
                (TimeSpan.FromSeconds 1.)
                "Overall cancellation interrupts accepting and idle header reads"
        finally
            listener.Stop()
    }

let private callbackMessages () =
    let check (body: byte array) (expected: string) =
        let source = Encoding.UTF8.GetString body
        let split = source.IndexOf("\r\n\r\n", StringComparison.Ordinal)
        Expect.isGreaterThan split 0 "HTTP response contains its header separator"
        let header = source.Substring(0, split)
        let text = source.Substring(split + 4)
        Expect.equal text expected "Response body preserves core-authored text"

        Expect.stringContains
            header
            "Content-Type: text/plain; charset=utf-8"
            "Plain UTF-8 rendering"

        Expect.stringContains
            header
            ("Content-Length: " + string (Encoding.UTF8.GetByteCount expected))
            "Length counts UTF-8 bytes"

    for accepted, identity in [ true, "cli.callbackAccepted"; false, "cli.callbackRefused" ] do
        check (LoopbackTransport.callbackResponse accepted) (ContractDiscovery.message identity)

    check (LoopbackTransport.response "200 OK" "Sintētisks — العربية") "Sintētisks — العربية"

[<Tests>]
let tests =
    testList
        "CLI maintained authentication"
        [
            testCase
                "loopback response renders generated core messages as framed UTF-8"
                callbackMessages
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
            testTask "an idle connection cannot monopolize the loopback callback" {
                do! unusedConnections true
            }
            testTask "an empty closed connection does not consume the loopback callback" {
                do! unusedConnections false
            }
            testTask "a partial callback header timeout is refused" {
                do! stalledPartialConnection ()
            }
            testTask "outer cancellation after a header byte remains cancellation" {
                do! cancelledPartialConnection ()
            }
            testTask "partial callback EOF is refused" { do! malformedConnection true }
            testTask "a complete malformed callback is refused" { do! malformedConnection false }
            testTask "an unused reset connection does not consume a callback" {
                do! resetConnection false
            }
            testTask "a reset after a header byte cannot be ignored" { do! resetConnection true }
            testTask "overall cancellation ends callback admission and idle header reads" {
                do! cancelledCallback false
                do! cancelledCallback true
            }
        ]
