module ClaimCore.ServerTests.AttachmentTransportTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Claims
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Server
open ClaimCore.TestSupport
open ClaimCore.Wire
open Expecto
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Primitives
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

let private boundary = "claimcore-test-boundary"

let private uploadManifest length digest =
    $"""{{"operationId":"10000000-0000-7000-8000-000000000001","task":"attachment.add","binding":{{"kind":"ATTACHMENT","target":{{"kind":"REFERENCE","caseReference":"CASE-1"}}}},"inputs":{{"input:attachmentName":"Arrival Photo.jpg","input:attachmentLength":"{length}","input:attachmentDigest":"{digest}"}}}}"""

let private abcDigest =
    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

let private emptyDigest =
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

let private part name filename media extra value =
    $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\nContent-Type: {media}\r\n{extra}\r\n{value}\r\n"

let private manifest value =
    part "manifest" "manifest.json" "application/json; charset=utf-8" "" value

let private file value =
    part "file" "payload.bin" "application/octet-stream" "" value

let private final = $"--{boundary}--\r\n"

/// Simulate a small-chunk non-seekable body with no declared Content-Length.
type private ChunkedBody(bytes: byte array) =
    inherit MemoryStream(bytes)
    override _.CanSeek = false

    override this.ReadAsync(buffer: Memory<byte>, token) =
        base.ReadAsync(buffer.Slice(0, min buffer.Length 7), token)

/// A streaming upload whose client controls when its bytes become available.
type private DeferredBody(release: Task, bytes: byte array) =
    inherit Stream()

    let source = ReadOnlyMemory<byte> bytes
    let mutable position = 0

    let entered =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.ReadStarted = entered.Task


    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, token) =
        ValueTask<int>(
            task {
                entered.TrySetResult() |> ignore
                do! release.WaitAsync token
                let count = min buffer.Length (source.Length - position)
                source.Slice(position, count).CopyTo buffer
                position <- position + count
                return count
            }
        )

let private parse (value: string) token =
    task {
        use body = new ChunkedBody(Encoding.UTF8.GetBytes value)

        return!
            AttachmentMultipart.read
                boundary
                Limits.MaximumRequestBytes
                Service.attachmentUploadIntent
                body
                token
    }

let private valid () =
    task {
        for length, digest, content in [ 3, abcDigest, "abc"; 0, emptyDigest, "" ] do
            let! result =
                parse
                    (manifest (uploadManifest length digest) + file content + final)
                    CancellationToken.None

            match result with
            | Ok(submission, bytes) ->
                Expect.equal
                    submission.OperationId
                    (Guid.Parse "10000000-0000-7000-8000-000000000001")
                    "Admits the complete upload manifest"

                Expect.sequenceEqual
                    (bytes.ToArray())
                    (Encoding.ASCII.GetBytes content)
                    "Preserves exact bytes, including an explicitly present empty part"
            | Error problem -> failtestf "Expected complete upload, got %A." problem
    }

let private invalid () =
    task {
        let header = manifest (uploadManifest 3 abcDigest)
        let payload = file "abc"

        for description, request in
            [
                "missing closing delimiter", header + payload
                "invalid closing delimiter", header + payload + $"--{boundary}-\r\n"
                "short file", header + file "ab" + final
                "extra byte", header + file "abcd" + final
                "missing file part", header + final
                "file before manifest", payload + header + final
                "duplicate manifest", header + header + payload + final
                "extra file", header + payload + payload + final
                "wrong filename",
                header + part "file" "arbitrary.txt" "application/octet-stream" "" "abc" + final
                "wrong file media type",
                header + part "file" "payload.bin" "text/html" "" "abc" + final
                "duplicate media type",
                header
                + part
                    "file"
                    "payload.bin"
                    "application/octet-stream"
                    "Content-Type: application/octet-stream\r\n"
                    "abc"
                + final
                "part encoding",
                header
                + part
                    "file"
                    "payload.bin"
                    "application/octet-stream"
                    "Content-Transfer-Encoding: base64\r\n"
                    "abc"
                + final
                "unbounded epilogue", header + payload + final + String('x', 15_000)
            ] do
            let! result = parse request CancellationToken.None
            Expect.isError result description
    }

let private headers () =
    let context = DefaultHttpContext()
    context.Request.ContentType <- "multipart/form-data; boundary=\"short-123\""

    Expect.equal
        (AttachmentMultipart.boundary context)
        (Some "short-123")
        "Quoted boundary parameter"

    for value in
        [
            "multipart/form-data"
            "multipart/form-data; boundary=x; boundary=y"
            "multipart/form-data; boundary=x; other=y"
            "multipart/form-data; boundary=" + String('x', 71)
        ] do
        context.Request.ContentType <- value
        Expect.isNone (AttachmentMultipart.boundary context) "Invalid boundary parameters"

    Expect.isTrue (AttachmentMultipart.acceptsIdentity context) "Unencoded representation is valid"
    context.Request.Headers.ContentEncoding <- "identity"

    Expect.isTrue
        (AttachmentMultipart.acceptsIdentity context)
        "An explicit identity encoding is valid"

    context.Request.Headers.ContentEncoding <- "gzip"
    Expect.isFalse (AttachmentMultipart.acceptsIdentity context) "Compressed multipart is refused"
    context.Request.Headers.ContentEncoding <- StringValues([| "identity"; "identity" |])
    Expect.isFalse (AttachmentMultipart.acceptsIdentity context) "Duplicated encoding is refused"

let private csrf () =
    task {
        let origin = Uri "https://localhost:5443"

        let oidc =
            {
                Issuer = Uri "https://issuer.example.test/realms/claimcore"
                TrustRoot = None
                ClientId = "claimcore-web"
                ClientSecret = "synthetic-client-secret"
                ApiAudience = "claimcore-api"
                CliClientId = "claimcore-cli"
            }

        let antiforgery =
            { new IAntiforgery with
                member _.GetAndStoreTokens _ = invalidOp "Not used"
                member _.GetTokens _ = invalidOp "Not used"
                member _.IsRequestValidAsync _ = invalidOp "Not used"

                member _.ValidateRequestAsync _ =
                    failtest "An invalid header must never invoke framework antiforgery."

                member _.SetCookieTokenAndHeader _ = ()
            }

        for tokens in
            [
                StringValues.Empty
                StringValues " "
                StringValues "token,token"
                StringValues [| "token"; "token" |]
            ] do
            let context = DefaultHttpContext()
            context.Request.Scheme <- "https"
            context.Connection.RemoteIpAddress <- IPAddress.Loopback
            context.Request.Headers.Host <- origin.Authority
            context.Request.Headers.Cookie <- "__Host-ClaimCoreSession=opaque"
            context.Request.Headers.Origin <- origin.GetLeftPart UriPartial.Authority
            context.Request.ContentType <- "multipart/form-data; boundary=x"
            context.Request.Headers[RequestGate.antiforgeryHeader] <- tokens

            let! result =
                RequestGate.actorUpload
                    (fun ctx -> (AttachmentMultipart.boundary ctx).IsSome)
                    oidc
                    (WebBindings.create origin "127.0.0.1" origin.Port)
                    (AttachmentTransport.maximumUploadBytes Limits.MaximumRequestBytes)
                    antiforgery
                    context

            Expect.equal
                result
                (Error GateFailure.AntiforgeryRejected)
                "Antiforgery rejects before form fallback"
    }

let private reserves () =
    let admission = AttachmentTransferAdmission()

    let principal name =
        PrincipalIdentity.human (Uri "https://issuer.example.test/") name
        |> function
            | Ok value -> ClaimCore.Server.AuthenticatedPrincipals.key value
            | Error failure -> failtestf "Fixture identity invalid: %A" failure

    let first = principal "first"
    let second = principal "second"
    let third = principal "third"

    use one = admission.TryAcquire first |> Option.get
    Expect.isTrue (admission.TryAcquire(first).IsNone) "One complete transfer per principal"
    use _two = admission.TryAcquire second |> Option.get
    Expect.isTrue (admission.TryAcquire(third).IsNone) "Two transfers per process and no queue"
    one.Dispose()
    use _replacement = admission.TryAcquire third |> Option.get
    one.Dispose()
    Expect.isTrue (admission.TryAcquire(first).IsNone) "Disposing twice cannot release another slot"

let private delivery () =
    task {
        let installation = Guid.Parse "10000000-0000-7000-8000-000000000001"
        let name = AttachmentName.parse "Résumé.pdf" |> Option.get

        let intent =
            AttachmentIntent.create name 3L (AttachmentDigest.parse abcDigest |> Option.get)
            |> Option.get

        let content =
            {
                AttachmentContent.Bytes =
                    VerifiedAttachment.verify
                        intent
                        (ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes "abc"))
                    |> Option.get
                Preview = None
            }

        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        use application = builder.Build()

        application.Run(
            RequestDelegate(fun context ->
                HttpHeaders.register (Uri "https://localhost:5443") context

                let transfer, kind =
                    match context.Request.Path.Value with
                    | "/png" -> AttachmentTransfer.Preview, Some AttachmentPreview.Png
                    | "/jpeg" -> AttachmentTransfer.Preview, Some AttachmentPreview.Jpeg
                    | "/pdf" -> AttachmentTransfer.Preview, Some AttachmentPreview.Pdf
                    | _ -> AttachmentTransfer.Download, None

                WebWire.attachment transfer { content with Preview = kind } installation
                |> fun response -> response.ExecuteAsync context)
        )

        do! application.StartAsync()
        use client = application.GetTestClient()
        use! response = client.PostAsync("/api/attachment.download", null)
        let! bytes = response.Content.ReadAsByteArrayAsync()

        Expect.equal response.StatusCode HttpStatusCode.OK "Complete verified response"

        Expect.equal
            (nonNull response.Content.Headers.ContentType).MediaType
            "application/octet-stream"
            "Download media"

        Expect.equal response.Content.Headers.ContentLength (Nullable 3L) "Exact length"

        Expect.sequenceEqual
            response.Content.Headers.ContentEncoding
            [ "identity" ]
            "Identity encoding"

        Expect.equal
            ((nonNull response.Content.Headers.ContentDisposition).ToString())
            "attachment; filename*=UTF-8''R%C3%A9sum%C3%A9.pdf"
            "Only the encoded canonical Unicode filename is sent"

        let header name =
            response.Headers.GetValues name |> Seq.exactlyOne

        Expect.equal
            (header "Content-Digest")
            "sha-256=:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=:"
            "RFC 9530 digest"

        Expect.equal
            (header "ClaimCore-Installation")
            (installation.ToString "D")
            "Confirmed installation"

        let cache = nonNull response.Headers.CacheControl
        Expect.isTrue cache.NoStore "Private bytes are not cacheable"
        Expect.isTrue cache.NoTransform "Private bytes cannot be transformed"

        Expect.equal (header "X-Content-Type-Options") "nosniff" "Binary response is not sniffed"
        Expect.sequenceEqual bytes [ 97uy; 98uy; 99uy ] "Bytes reach the socket unchanged"

        for path, mediaType in
            [ "/png", "image/png"; "/jpeg", "image/jpeg"; "/pdf", "application/pdf" ] do
            use! preview = client.PostAsync(path, null)
            let! payload = preview.Content.ReadAsByteArrayAsync()
            Expect.equal preview.StatusCode HttpStatusCode.OK "Complete preview response"

            Expect.equal
                (nonNull preview.Content.Headers.ContentType).MediaType
                mediaType
                "Preview media follows the Core kind"

            Expect.isNull
                preview.Content.Headers.ContentDisposition
                "Preview is delivered without a save name"

            Expect.equal
                preview.Content.Headers.ContentLength
                (Nullable 3L)
                "Preview has an exact length"

            Expect.equal
                (preview.Headers.GetValues("Content-Digest") |> Seq.exactlyOne)
                (header "Content-Digest")
                "Preview has the verified digest"

            Expect.equal
                (preview.Headers.GetValues("ClaimCore-Installation") |> Seq.exactlyOne)
                (header "ClaimCore-Installation")
                "Preview has its installation"

            Expect.sequenceEqual
                preview.Content.Headers.ContentEncoding
                [ "identity" ]
                "Preview is untransformed"

            let previewCache = nonNull preview.Headers.CacheControl

            Expect.isTrue
                (previewCache.NoStore && previewCache.NoTransform)
                "Preview keeps binary cache controls"

            Expect.sequenceEqual payload bytes "Preview retains exact verified bytes"
    }

let private headerCookies (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values -> nonNull values |> Seq.map (fun value -> value.Split(';', 2)[0]) |> Seq.toList
    | _ -> []

type private BrowserTransfer = { Cookie: string; Token: string }

let private browserTransfer (host: TestServerFixture.Host) subject =
    task {
        use! login =
            host.SendResponse(HttpMethod.Post, "/__test/login?subject=" + subject, None, [])

        Expect.equal login.StatusCode HttpStatusCode.OK "Signed-in browser session"

        // Bootstrap each signed cookie independently so a prior test principal's cookie
        // cannot affect the antiforgery context of another.
        let loginCookies = headerCookies login
        use request = new HttpRequestMessage(HttpMethod.Get, "/api/session")
        request.Headers.Host <- TestServerFixture.origin.Authority
        request.Headers.Add("Cookie", String.concat "; " loginCookies)
        request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
        use! bootstrap = host.Client.SendAsync request

        Expect.equal
            bootstrap.StatusCode
            HttpStatusCode.OK
            "Independently bootstrapped browser session"

        let! bytes = bootstrap.Content.ReadAsByteArrayAsync()
        use json = JsonDocument.Parse bytes

        return
            {
                Cookie = loginCookies @ headerCookies bootstrap |> String.concat "; "
                Token =
                    json.RootElement
                        .GetProperty("outcome")
                        .GetProperty("data")
                        .GetProperty("antiforgeryToken")
                        .GetString()
                    |> nonNull
            }
    }

type private CountedBody(bytes: byte array) =
    inherit MemoryStream(bytes)

    let mutable reads = 0

    member _.Reads = Volatile.Read &reads

    override _.Read(buffer: byte array, offset, count) =
        Interlocked.Increment &reads |> ignore
        base.Read(buffer, offset, count)

    override _.ReadAsync(buffer: byte array, offset, count, token) =
        Interlocked.Increment &reads |> ignore
        base.ReadAsync(buffer, offset, count, token)

    override _.ReadAsync(buffer: Memory<byte>, token) =
        Interlocked.Increment &reads |> ignore
        base.ReadAsync(buffer, token)

let private directUpload
    (server: TestServer)
    browser
    (requestBody: Stream)
    (change: HttpRequest -> unit)
    (cancellation: CancellationToken)
    =
    let origin = TestServerFixture.origin

    server.SendAsync(
        (fun context ->
            let request = context.Request
            request.Method <- "POST"
            request.Path <- PathString "/api/attachment.upload"
            request.Scheme <- "https"
            request.Headers.Host <- origin.Authority
            request.Headers.Origin <- origin.GetLeftPart UriPartial.Authority
            request.Headers["ClaimCore-Contract"] <- ContractIdentity.value
            request.Headers["ClaimCore-Installation"] <- HandlerStub.operationId.ToString("D")
            request.Headers.Cookie <- browser.Cookie
            request.Headers[RequestGate.antiforgeryHeader] <- browser.Token
            request.ContentType <- $"multipart/form-data; boundary={boundary}"
            request.Body <- requestBody
            change request),
        cancellation
    )

let private protectedIngress () =
    task {
        let installation = Guid.NewGuid()

        use host = new TestServerFixture.Host(true)

        let! browser = browserTransfer host "synthetic-ingress"
        let server = host.Services.GetRequiredService<IServer>() :?> TestServer

        let send change =
            task {
                use requestBody =
                    new CountedBody(Encoding.ASCII.GetBytes "not a complete multipart frame")

                let! context = directUpload server browser requestBody change CancellationToken.None

                use reply = context.Response.Body
                do! reply.CopyToAsync(Stream.Null)
                return context.Response.StatusCode, requestBody.Reads
            }

        for label, change, status in
            [
                "wrong contract",
                (fun (request: HttpRequest) -> request.Headers["ClaimCore-Contract"] <- "foreign"),
                409
                "installation mismatch",
                (fun request ->
                    request.Headers["ClaimCore-Installation"] <- installation.ToString("D")),
                409
                "wrong Origin",
                (fun request -> request.Headers.Origin <- "https://foreign.invalid"),
                403
                "missing credentials",
                (fun request -> request.Headers.Remove "Cookie" |> ignore),
                401
                "mixed credentials",
                (fun request -> request.Headers.Authorization <- "Bearer synthetic"),
                401
                "missing antiforgery",
                (fun request -> request.Headers.Remove RequestGate.antiforgeryHeader |> ignore),
                403
                "duplicated antiforgery",
                (fun request ->
                    request.Headers[RequestGate.antiforgeryHeader] <-
                        StringValues [| browser.Token; browser.Token |]),
                403
            ] do
            let! observed, reads = send change
            Expect.equal observed status label
            Expect.equal reads 0 (label + " must refuse before reading even one body byte")

        let! invalidFrame, reads = send ignore
        Expect.equal invalidFrame 400 "Valid admission reaches multipart validation"
        Expect.isGreaterThan reads 0 "Independent control proves the route reads the counted body"
    }

let private authenticatedTransferLimit () =
    task {
        use host = new TestServerFixture.Host(true)
        let! first = browserTransfer host "pressure-first"
        let! second = browserTransfer host "pressure-second"
        let! third = browserTransfer host "pressure-third"
        let origin = TestServerFixture.origin
        let admission = host.Services.GetRequiredService<AttachmentTransferAdmission>()

        let principal subject =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri subject
            |> Result.defaultWith (string >> failtest)

        let slotAvailable key =
            match admission.TryAcquire key with
            | Some lease ->
                lease.Dispose()
                true
            | None -> false

        let waitForSlot key expected =
            task {
                use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.)

                while slotAvailable key <> expected do
                    do! Task.Delay(10, deadline.Token)
            }

        let send credentials (body: Stream) (cancellation: CancellationToken) =
            task {
                use request = new HttpRequestMessage(HttpMethod.Post, "/api/attachment.upload")
                request.Headers.Host <- origin.Authority
                request.Headers.Add("Origin", origin.GetLeftPart UriPartial.Authority)
                request.Headers.Add("Cookie", credentials.Cookie)
                request.Headers.Add("X-ClaimCore-Antiforgery", credentials.Token)
                request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
                request.Headers.Add("ClaimCore-Installation", HandlerStub.operationId.ToString("D"))
                use content = new StreamContent(body)

                content.Headers.ContentType <-
                    MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}")

                request.Content <- content
                use! response = host.Client.SendAsync(request, cancellation)
                return int response.StatusCode
            }

        let withheld =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let hold credentials (cancelled: CancellationTokenSource) =
            task {
                try
                    let! _ =
                        send
                            credentials
                            (new DeferredBody(withheld.Task, Array.empty))
                            cancelled.Token

                    return false
                with :? OperationCanceledException ->
                    return true
            }

        let frame = Encoding.ASCII.GetBytes "incomplete frame"
        let firstKey = principal "pressure-first"
        let secondKey = principal "pressure-second"
        use releaseFirst = new CancellationTokenSource()
        use releaseSecond = new CancellationTokenSource()
        let waitingFirst = hold first releaseFirst
        let waitingSecond = hold second releaseSecond

        try
            do! waitForSlot firstKey false
            do! waitForSlot secondKey false

            use retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
            let! excess = send third (new ChunkedBody(frame)) retryDeadline.Token

            Expect.equal
                excess
                429
                "Two distinct authenticated uploads leave no global slot or queue"

            releaseFirst.Cancel()
            let! firstAborted = waitingFirst

            Expect.isTrue
                firstAborted
                "The first client canceled while the second still occupies a slot"

            do! waitForSlot firstKey true

            let! retry = send third (new ChunkedBody(frame)) retryDeadline.Token

            Expect.equal
                retry
                400
                "The third principal reaches parsing after the first slot releases"
        finally
            releaseFirst.Cancel()
            releaseSecond.Cancel()

        let! secondAborted = waitingSecond
        Expect.isTrue secondAborted "The second blocked client also cancels"
        do! waitForSlot secondKey true
    }

let private shutdownTransfer () =
    task {
        use host = new TestServerFixture.Host(true)
        let subject = "synthetic-shutdown"
        let! browser = browserTransfer host subject
        let server = host.Services.GetRequiredService<IServer>() :?> TestServer
        let lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>()
        let admission = host.Services.GetRequiredService<AttachmentTransferAdmission>()

        let principal =
            PrincipalKey.human TestServerFixture.oidc.Issuer.AbsoluteUri subject
            |> Result.defaultWith (string >> failtest)

        let withheld =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use body = new DeferredBody(withheld.Task, Array.empty)
        use cleanup = new CancellationTokenSource()
        let request = directUpload server browser body ignore cleanup.Token
        let wait = TimeSpan.FromSeconds 5.

        try
            do! body.ReadStarted.WaitAsync wait

            match admission.TryAcquire principal with
            | Some lease ->
                lease.Dispose()
                failtest "The test must reach a body read while holding its transfer slot."
            | None -> ()

            Expect.isFalse request.IsCompleted "The streaming request is still waiting on its body"

            Expect.isFalse
                lifetime.ApplicationStopping.IsCancellationRequested
                "The host is initially running"

            lifetime.StopApplication()

            let! terminated =
                task {
                    try
                        let! response = request.WaitAsync wait
                        use returned = response.Response.Body
                        do! returned.CopyToAsync(Stream.Null).WaitAsync wait
                        return false
                    with
                    | :? OperationCanceledException
                    | :? IOException -> return true
                }

            Expect.isTrue terminated "Host shutdown terminates the admitted transfer"
            Expect.isFalse cleanup.IsCancellationRequested "The client did not cancel the request"
            Expect.isFalse withheld.Task.IsCompleted "The client never sent the rest of the body"

            use releaseDeadline = new CancellationTokenSource(wait)
            let mutable released = false

            while not released do
                match admission.TryAcquire principal with
                | None -> do! Task.Delay(10, releaseDeadline.Token)
                | Some lease ->
                    lease.Dispose()
                    released <- true
        finally
            cleanup.Cancel()
            withheld.TrySetResult() |> ignore
    }

let private maximumKestrelFrame () =
    task {
        use certificate = ProbeHttpsFixture.certificate ()
        let origin = Uri "https://probe.localhost:9443"
        let baseConfiguration = TestServerFixture.configuration certificate

        let configuration =
            { baseConfiguration with
                Binding = WebBindings.create origin "127.0.0.1" 9443
            }

        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore

        builder.WebHost.ConfigureKestrel(fun options ->
            // Match the ordinary production body limit. This test never pre-lifts it.
            options.Limits.MaxRequestBodySize <- int64 Limits.MaximumRequestBytes

            options.Listen(
                IPAddress.Loopback,
                0,
                fun listen -> listen.UseHttps certificate |> ignore
            ))
        |> ignore

        builder.Services.AddAntiforgery() |> ignore
        RateLimits.configure configuration.RequestGate builder.Services

        let authentication =
            { new IAuthenticationService with
                member _.AuthenticateAsync(context, scheme) =
                    if
                        scheme = AuthMiddleware.BearerScheme
                        && string context.Request.Headers.Authorization = "Bearer synthetic-kestrel"
                    then
                        let identity =
                            ClaimsIdentity(
                                [ Claim("sub", "synthetic-kestrel"); Claim("azp", "cli") ],
                                scheme
                            )

                        Task.FromResult(
                            AuthenticateResult.Success(
                                AuthenticationTicket(
                                    ClaimsPrincipal identity,
                                    AuthMiddleware.BearerScheme
                                )
                            )
                        )
                    else
                        Task.FromResult(AuthenticateResult.NoResult())

                member _.ChallengeAsync(_, _, _) = Task.CompletedTask
                member _.ForbidAsync(_, _, _) = Task.CompletedTask
                member _.SignInAsync(_, _, _, _) = Task.CompletedTask
                member _.SignOutAsync(_, _, _) = Task.CompletedTask
            }

        builder.Services.AddSingleton<IAuthenticationService>(authentication) |> ignore
        let stub = HandlerStub.Stub()
        use application = builder.Build()

        let invoke
            (principal: PrincipalKey)
            (action: ServiceContext -> Task<Result<RouteReply, HostFailureReason>>)
            =
            task {
                let! accepted = action (stub.InvocationContext principal)
                return Some accepted
            }

        application.MapPost(
            "/api/attachment.upload",
            Func<HttpContext, Task>(
                AttachmentRoutes.handle AttachmentTransfer.Upload configuration invoke
            )
        )
        |> ignore

        do! application.StartAsync()

        try
            let port = application.Urls |> Seq.exactlyOne |> Uri |> _.Port

            let socket =
                { configuration.Binding with
                    ListenPort = port
                }

            use transport = WebProbeTransport.create socket (Some certificate)
            use client = new HttpClient(transport, BaseAddress = origin)

            let expectedFileLength = 26_214_400
            let digest = "394c345f0b0c63ee652627a62eed069244d35c4d5134e4f07d4eabb51afda47e"
            let partHeader = file ""

            let prefix =
                manifest (uploadManifest expectedFileLength digest)
                + partHeader.Substring(0, partHeader.Length - 2)
                |> Encoding.ASCII.GetBytes

            let suffix = Encoding.ASCII.GetBytes("\r\n" + final)

            let frame length =
                let bytes = Array.zeroCreate<byte>(prefix.Length + length + suffix.Length)
                prefix.CopyTo(bytes, 0)
                suffix.CopyTo(bytes, prefix.Length + length)
                bytes

            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 25.)

            let send (body: HttpContent) (expectContinue: bool) =
                task {
                    use request = new HttpRequestMessage(HttpMethod.Post, "/api/attachment.upload")
                    request.Headers.Add("Authorization", "Bearer synthetic-kestrel")
                    request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)

                    request.Headers.Add(
                        "ClaimCore-Installation",
                        HandlerStub.operationId.ToString("D")
                    )

                    request.Headers.ExpectContinue <- Nullable expectContinue

                    body.Headers.ContentType <-
                        MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}")

                    request.Content <- body

                    use! response = client.SendAsync(request, deadline.Token)
                    let! reply = response.Content.ReadAsByteArrayAsync deadline.Token
                    return int response.StatusCode, reply
                }

            let stream length =
                new StreamContent(new DeferredBody(Task.CompletedTask, frame length))

            let! accepted, acceptedBody = send (stream expectedFileLength) false
            Expect.equal accepted 200 "Real HTTPS Kestrel admits a chunked exact-maximum file"

            Expect.isOk
                (Codec.decode Limits.MaximumResponseBytes Api.operationExecute.Output acceptedBody)
                "The body reaches the typed application reply through the production route"

            let! fileOver, fileFailure = send (stream (expectedFileLength + 1)) false

            Expect.equal
                fileOver
                413
                "One extra streamed file byte crosses the independent file bound"

            Expect.isOk (HostFailureCodecs.decode fileFailure) "Over-file rejection stays typed"

            let! frameOver, _ = send (new ByteArrayContent(Array.zeroCreate 26_284_573)) true

            Expect.equal
                frameOver
                413
                "A declared frame one byte above the default aggregate limit refuses"
        finally
            application.StopAsync().GetAwaiter().GetResult()
    }

let private uploadOperation (operation: Guid) =
    (uploadManifest 3 abcDigest)
        .Replace("10000000-0000-7000-8000-000000000001", operation.ToString "D")
        .Replace("CASE-1", "SYNTHETIC-001")

let private hostTransfer () =
    task {
        let origin = ClaimCore.TestSupport.TestServerFixture.origin
        let issuer = ClaimCore.TestSupport.TestServerFixture.oidc.Issuer
        use! database = OidcCoreFixture.createFor issuer.AbsoluteUri "synthetic-human"

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(
                true,
                forActor = database.Actor,
                installationId = database.InstallationId
            )

        use! login = host.SendResponse(HttpMethod.Post, "/__test/login", None, [])
        Expect.equal login.StatusCode HttpStatusCode.OK "Browser session is admitted"

        use! bootstrap =
            host.SendResponse(
                HttpMethod.Get,
                "/api/session",
                None,
                [ "ClaimCore-Contract", ContractIdentity.value ]
            )

        let cookies = headerCookies login @ headerCookies bootstrap |> String.concat "; "
        let! bootstrapBytes = bootstrap.Content.ReadAsByteArrayAsync()
        use bootstrapJson = JsonDocument.Parse bootstrapBytes

        let token =
            bootstrapJson.RootElement
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("antiforgeryToken")
                .GetString()
            |> nonNull

        let transmit (installation: Guid) (stream: Stream) (cancellation: CancellationToken) =
            task {
                use request = new HttpRequestMessage(HttpMethod.Post, "/api/attachment.upload")
                request.Headers.Host <- origin.Authority
                request.Headers.Add("Origin", origin.GetLeftPart UriPartial.Authority)
                request.Headers.Add("Cookie", cookies)
                request.Headers.Add("X-ClaimCore-Antiforgery", token)
                request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
                request.Headers.Add("ClaimCore-Installation", installation.ToString("D"))

                use content = new StreamContent(stream)

                content.Headers.ContentType <-
                    MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}")

                request.Content <- content

                use! response = host.Client.SendAsync(request, cancellation)
                let! reply = response.Content.ReadAsByteArrayAsync(cancellation)
                return response.StatusCode, reply
            }

        let encode operation body =
            manifest (uploadOperation operation) + file body + final
            |> Encoding.UTF8.GetBytes

        let upload operation body installation =
            transmit installation (new ChunkedBody(encode operation body)) CancellationToken.None

        let admission = host.Services.GetRequiredService<AttachmentTransferAdmission>()

        let actor =
            PrincipalKey.human issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let slotAvailable () =
            match admission.TryAcquire actor with
            | Some lease ->
                lease.Dispose()
                true
            | None -> false

        let waitForSlot expected =
            task {
                use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.)

                while slotAvailable () <> expected do
                    do! Task.Delay(10, deadline.Token)
            }

        let before = database.EntryCount

        use cancelled = new CancellationTokenSource()

        let withheld =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let stalled =
            task {
                try
                    let! _ =
                        transmit
                            database.InstallationId
                            (new DeferredBody(withheld.Task, Array.empty))
                            cancelled.Token

                    return false
                with :? OperationCanceledException ->
                    return true
            }

        try
            do! waitForSlot false
            let! competing, _ = upload (Guid.CreateVersion7()) "abc" database.InstallationId

            Expect.equal
                competing
                HttpStatusCode.TooManyRequests
                "Same principal cannot start a second transfer"

            let work = host.Services.GetRequiredService<PrincipalAdmission>()

            use writeWait = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
            let! writePermit = work.Acquire(actor, RequestWorkload.Write, writeWait.Token)

            match writePermit with
            | None ->
                failtest
                    "A stalled transfer must leave the same principal's storage write permit available."
            | Some permit -> permit.Dispose()

            let! ordinary, _ = host.Call("context", Encoding.UTF8.GetBytes "{}", token)

            Expect.equal
                ordinary
                200
                "A body waiting on the client leaves ordinary case capacity available"

            let! oversized, _ =
                host.Call("context", Array.zeroCreate (Limits.MaximumRequestBytes + 1), token)

            Expect.equal oversized 413 "Transfer admission leaves the ordinary body limit intact"
        finally
            cancelled.Cancel()

        let! aborted = stalled
        Expect.isTrue aborted "A stalled transfer is cancellable"
        do! waitForSlot true
        Expect.equal database.EntryCount before "A cancelled transfer cannot append"

        let operation = Guid.CreateVersion7()
        let! wrongStatus, _ = upload operation "abc" (Guid.NewGuid())

        Expect.equal
            wrongStatus
            HttpStatusCode.Conflict
            "Installation mismatch is refused before the transfer"

        let! mismatchStatus, mismatchBytes = upload operation "xyz" database.InstallationId

        Expect.equal
            mismatchStatus
            HttpStatusCode.OK
            "A complete transfer with a wrong digest returns a typed refusal"

        let decode expectedOperation bytes =
            match Codec.decode Limits.MaximumResponseBytes Api.operationExecute.Output bytes with
            | Ok(RepresentationReply.Data reply) ->
                Expect.equal
                    reply.OperationId
                    expectedOperation
                    "Multipart shares the operation identity echo"

                reply.Outcome
            | _ -> failtest "Expected a represented execute reply."

        match decode operation mismatchBytes with
        | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
        | result -> failtestf "Digest mismatch was not rejected: %A" result

        Expect.equal database.EntryCount before "A refused transfer cannot append"

        let blockedOperation = Guid.CreateVersion7()

        let blockedManifest =
            (uploadOperation blockedOperation).Replace("Arrival Photo.jpg", "Arrival.exe")

        let blockedFrame =
            manifest blockedManifest + file "abc" + final |> Encoding.UTF8.GetBytes

        let! blockedStatus, blockedBytes =
            transmit database.InstallationId (new ChunkedBody(blockedFrame)) CancellationToken.None

        Expect.equal
            blockedStatus
            HttpStatusCode.OK
            "Filename policy is not a multipart framing failure"

        Expect.equal
            (decode blockedOperation blockedBytes)
            (ExecuteResult.Rejected(
                Refusal.invalidInput (InputTarget.AttachmentName, Violation.AttachmentTypeRefused)
            ))
            "A blocked name reaches the authorized execution decision"

        Expect.equal database.EntryCount before "A blocked filename cannot append"

        let release =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use slowDeadline = new CancellationTokenSource(TimeSpan.FromSeconds 35.)

        let slowUpload =
            transmit
                database.InstallationId
                (new DeferredBody(release.Task, encode operation "abc"))
                slowDeadline.Token

        try
            do! waitForSlot false
            do! Task.Delay(TimeSpan.FromSeconds 21., slowDeadline.Token)

            Expect.isFalse
                (slotAvailable ())
                "The transfer survives the ordinary transfer deadlines"
        finally
            release.TrySetResult() |> ignore

        let! status, bytes = slowUpload

        Expect.equal
            status
            HttpStatusCode.OK
            "Complete authenticated multipart is admitted within its own deadline"

        match decode operation bytes with
        | ExecuteResult.Accepted _ -> ()
        | result -> failtestf "Expected upload acceptance: %A" result

        let! _, replay = upload operation "abc" database.InstallationId

        match decode operation replay with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | result -> failtestf "Expected repeat upload to replay: %A" result

        Expect.equal database.EntryCount (before + 1) "Retries record one operation"

        let selector =
            AttachmentTransport.request.Write
                {
                    Target = CaseSelector.Reference "SYNTHETIC-001"
                    AttachmentId = AttachmentId.create operation |> Option.get
                }
            |> WireWriter.bytes

        use! response = host.CallResponse("attachment.download", selector, token)
        let! saved = response.Content.ReadAsByteArrayAsync()
        Expect.equal response.StatusCode HttpStatusCode.OK "Complete byte read"

        Expect.equal
            ((nonNull response.Content.Headers.ContentType).MediaType)
            "application/octet-stream"
            "Verified media"

        Expect.sequenceEqual saved [ 97uy; 98uy; 99uy ] "Protected download preserves exact bytes"

        use! preview = host.CallResponse("attachment.preview", selector, token)
        let! previewBytes = preview.Content.ReadAsByteArrayAsync()

        Expect.equal
            ((nonNull preview.Content.Headers.ContentType).MediaType)
            "application/json"
            "Unavailable preview remains a typed refusal"

        match Codec.decode Limits.MaximumResponseBytes AttachmentTransport.failure previewBytes with
        | Ok(AttachmentTransferFailure.Rejected(ClaimCore.Presentation.HumanRejection.Refused advice)) ->
            Expect.equal
                advice.Message.MessageId
                "diagnostic.ATTACHMENT_PREVIEW_UNAVAILABLE"
                "Preview refusal has its own Core wording"
        | other -> failtestf "Expected preview-unavailable refusal: %A" other
    }

[<Tests>]
let tests =
    testList
        "attachment transfer"
        [
            testCaseTask
                "authenticated multipart, upload replay and binary content use real storage"
                hostTransfer
            testCaseTask
                "admits exactly one manifest and file with complete streaming framing"
                valid
            testCaseTask "rejects invalid parts, lengths and terminal framing" invalid
            testCase "bounds recognized multipart headers and requires identity encoding" headers
            testCaseTask
                "rejects absent, duplicated and combined antiforgery headers before framework fallback"
                csrf
            testCase "reserves the whole process transfer budget without queuing" reserves
            testCaseTask
                "protected upload refuses invalid admission with zero body reads"
                protectedIngress
            testCaseTask
                "two authenticated uploads exclude a third until cancellation"
                authenticatedTransferLimit
            testCaseTask "delivers exact bytes with complete-object HTTP headers" delivery
            testCaseTask
                "ephemeral HTTPS Kestrel admits exact-max chunked payload and rejects over limits"
                maximumKestrelFrame
            testCaseTask
                "application shutdown terminates a stalled upload and releases its transfer lease"
                shutdownTransfer
        ]
