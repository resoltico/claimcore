module ClaimCore.ServerTests.DigestSmtpTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open ClaimCore.Server
open ClaimCore.Domain
open ClaimCore.TestSupport.MailpitFixture
open Expecto

let private sendsOnlyThroughTls tls =
    task {
        use timeout = new CancellationTokenSource(TimeSpan.FromMinutes 2.)
        let ct = timeout.Token
        use fixture = new Mailpit(tls, Some "@allowed[.]example[.]test$")
        do! fixture.Start ct

        let smtp = fixture.Smtp
        let recipient = "recipient@allowed.example.test"
        let subject = "1 reminder"
        let body = "1 overdue deadline\nhttps://claims.example.test/reminders?scope=mine"

        let! untrusted =
            DigestSmtp.send { smtp with TrustRoot = None } recipient subject body ct

        Expect.equal untrusted DigestRelayResult.Refused "The private certificate requires its root"

        let! sent = DigestSmtp.send smtp recipient subject body ct
        Expect.equal sent DigestRelayResult.Accepted "The relay acknowledged the message"

        let! denied = DigestSmtp.send smtp "denied@example.test" subject body ct
        Expect.equal denied DigestRelayResult.Refused "SMTP refused the recipient"

        use http = new HttpClient(BaseAddress = fixture.Api)
        let! messages = http.GetStringAsync("messages", ct)
        use response = JsonDocument.Parse messages
        let items = response.RootElement.GetProperty "messages"
        Expect.equal (items.GetArrayLength()) 1 "Exactly the accepted message was stored"
        let id = items[0].GetProperty("ID").GetString() |> nonNull
        let! message = http.GetStringAsync("message/" + id, ct)
        use response = JsonDocument.Parse message
        let data = response.RootElement
        Expect.equal (data.GetProperty("Subject").GetString()) subject "Subject preserved"

        let text = data.GetProperty("Text").GetString() |> nonNull
        Expect.equal (text.Replace("\r\n", "\n").TrimEnd()) body "Plain-text body preserved"
    }

let private losesDataAcknowledgement () =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 15.)
        let token = deadline.Token
        use root = rootCertificate ()
        use key = RSA.Create 2048
        use leaf = serverCertificate root key
        use certificate = leaf.CopyWithPrivateKey key
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()

        let relay =
            task {
                use! socket = listener.AcceptTcpClientAsync(token)
                use tls = new SslStream(socket.GetStream())

                do!
                    tls.AuthenticateAsServerAsync(
                        SslServerAuthenticationOptions(ServerCertificate = certificate),
                        token
                    )

                use reader = new StreamReader(tls, leaveOpen = true)
                use writer = new StreamWriter(tls, leaveOpen = true)
                writer.NewLine <- "\r\n"
                writer.AutoFlush <- true

                do! writer.WriteLineAsync("220 synthetic relay")
                let mutable preparing = true

                while preparing do
                    let! command = reader.ReadLineAsync(token)

                    match Option.ofObj command with
                    | Some command when command.StartsWith("EHLO ", StringComparison.Ordinal) ->
                        do! writer.WriteLineAsync("250 synthetic relay")
                    | Some command when
                        command.StartsWith("MAIL FROM:", StringComparison.Ordinal)
                        || command.StartsWith("RCPT TO:", StringComparison.Ordinal)
                        ->
                        do! writer.WriteLineAsync("250 accepted")
                    | Some "DATA" ->
                        preparing <- false
                        do! writer.WriteLineAsync("354 send message")
                    | _ -> failtest "Unexpected SMTP command"

                let mutable completed = false
                let mutable lines = 0

                while not completed do
                    let! line = reader.ReadLineAsync(token)

                    match Option.ofObj line with
                    | Some "." -> completed <- true
                    | Some _ -> lines <- lines + 1
                    | None -> failtest "The relay never received the complete SMTP message"

                // The relay committed the message on receiving the terminator. Close TCP before
                // its final 250 reaches the sender.
                socket.Client.Shutdown(SocketShutdown.Both)
                return lines
            }

        let smtp =
            {
                Host = "127.0.0.1"
                Port = (listener.LocalEndpoint :?> IPEndPoint).Port
                Tls = DigestSmtpTls.Implicit
                Credentials = None
                TrustRoot = Some root
                From = MimeKit.MailboxAddress("", "digest@example.test")
            }

        let! result =
            DigestSmtp.send
                smtp
                "recipient@example.test"
                "Synthetic digest"
                "Synthetic aggregate counts"
                token

        let! acceptedLines = relay
        Expect.isGreaterThan acceptedLines 0 "The TLS relay received a complete message"

        Expect.equal
            result
            DigestRelayResult.Uncertain
            "A lost final SMTP response gives uncertain delivery"
    }

[<Tests>]
let tests =
    testList
        "digest SMTP transport"
        [
            testTask "losing the final SMTP response after DATA is uncertain" {
                do! losesDataAcknowledgement ()
            }
            testTask "STARTTLS verifies the root, sends plain text and reports rejection" {
                do! sendsOnlyThroughTls DigestSmtpTls.StartTls
            }
            testTask "implicit TLS verifies the root, sends plain text and reports rejection" {
                do! sendsOnlyThroughTls DigestSmtpTls.Implicit
            }
        ]
    |> testSequenced
