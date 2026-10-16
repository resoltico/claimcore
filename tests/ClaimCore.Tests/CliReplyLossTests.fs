module ClaimCore.Tests.CliReplyLossTests

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.ClientContract
open CliTransportFixture

let private certificate () =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    let names = SubjectAlternativeNameBuilder()
    names.AddIpAddress IPAddress.Loopback
    request.CertificateExtensions.Add(names.Build())

    request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1.),
        DateTimeOffset.UtcNow.AddMinutes(10.)
    )

let private receiveRequest (stream: SslStream) (cancelled: CancellationToken) =
    task {
        use headers = new MemoryStream()
        let one = Array.zeroCreate<byte> 1
        let mutable complete = false

        while not complete && headers.Length < 8192L do
            let! count = stream.ReadAsync(one.AsMemory(), cancelled)

            if count = 0 then
                failwith "Request ended before headers."

            headers.WriteByte one[0]
            let current = headers.ToArray()

            complete <-
                current.Length >= 4
                && current[current.Length - 4 ..] = [| 13uy; 10uy; 13uy; 10uy |]

        if not complete then
            failwith "Request headers exceeded fixture bound."

        let text = Encoding.ASCII.GetString(headers.ToArray())

        let length =
            text.Split("\r\n")
            |> Array.pick (fun line ->
                if line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                    Some(Int32.Parse((line.Split(':')[1]).Trim()))
                else
                    None)

        let body = Array.zeroCreate<byte> length
        do! stream.ReadExactlyAsync(body.AsMemory(), cancelled)
        return text, body
    }

let private physicalReplyLoss () =
    use signed = certificate ()
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 10.)

    let received =
        task {
            use! socket = listener.AcceptTcpClientAsync(timeout.Token)
            use secure = new SslStream(socket.GetStream(), false)

            do!
                secure.AuthenticateAsServerAsync(
                    SslServerAuthenticationOptions(ServerCertificate = signed),
                    timeout.Token
                )

            let! request = receiveRequest secure timeout.Token
            // Disposing the TLS connection delivers no HTTP status or response body.
            return request
        }

    use handler = new HttpClientHandler(AllowAutoRedirect = false)

    handler.ServerCertificateCustomValidationCallback <-
        Func<_, _, _, _, _>(fun _ presented _ errors ->
            match presented with
            | null -> false
            | presented ->
                errors = SslPolicyErrors.RemoteCertificateChainErrors
                && presented.RawData = signed.RawData)

    use client = new HttpClient(handler)
    client.Timeout <- TimeSpan.FromSeconds 5.
    let invocation = frame "authority.execute"
    let mutable dispatched = false

    let reply =
        RemoteServiceCall.send
            client
            (Uri($"https://127.0.0.1:{port}/"))
            invocation
            (AuthenticationHeaderValue("Bearer", "synthetic-token"))
            (fun () -> dispatched <- true)
            timeout.Token
        |> _.GetAwaiter().GetResult()

    let headers, body = received.GetAwaiter().GetResult()
    Expect.isTrue dispatched "Dispatch marker was reached"

    Expect.stringStarts
        headers
        "POST /api/authority.execute HTTP/1.1"
        "Actual mutation reached peer"

    Expect.equal body (Frames.inputBytes invocation) "Entire original request reached peer"
    Expect.equal reply.ExitCode 4 "Reply loss cannot establish commit outcome"
    Expect.isOk (Frames.decode reply.Bytes) "Uncertainty has an admitted reply"

[<Tests>]
let tests =
    testList
        "CLI TLS reply loss"
        [
            testCase
                "complete dispatched mutation and dropped TLS reply produce exit four"
                physicalReplyLoss
        ]
