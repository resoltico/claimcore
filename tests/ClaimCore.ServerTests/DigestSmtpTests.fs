module ClaimCore.ServerTests.DigestSmtpTests

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open ClaimCore.Server
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
            task {
                try
                    do! DigestSmtp.send { smtp with TrustRoot = None } recipient subject body ct
                    return false
                with :? MailKit.Security.SslHandshakeException ->
                    return true
            }

        Expect.isTrue untrusted "Trust validation rejects the private certificate without its root"

        do! DigestSmtp.send smtp recipient subject body ct

        let! denied =
            task {
                try
                    do! DigestSmtp.send smtp "denied@example.test" subject body ct
                    return false
                with :? MailKit.Net.Smtp.SmtpCommandException ->
                    return true
            }

        Expect.isTrue denied "SMTP refused the recipient"

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

[<Tests>]
let tests =
    testList
        "digest SMTP transport"
        [
            testTask "STARTTLS verifies the root, sends plain text and reports rejection" {
                do! sendsOnlyThroughTls DigestSmtpTls.StartTls
            }
            testTask "implicit TLS verifies the root, sends plain text and reports rejection" {
                do! sendsOnlyThroughTls DigestSmtpTls.Implicit
            }
        ]
    |> testSequenced
