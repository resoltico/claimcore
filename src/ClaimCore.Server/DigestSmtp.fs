namespace ClaimCore.Server

open System
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Threading
open ClaimCore.HostSecurity
open MailKit
open MailKit.Net.Smtp
open MailKit.Security
open MimeKit

module internal DigestSmtp =
    type private Client(root: X509Certificate2 option) =
        inherit SmtpClient()

        override _.GetSslClientAuthenticationOptions
            (host, validation: RemoteCertificateValidationCallback)
            =
            let options = base.GetSslClientAuthenticationOptions(host, validation)

            root
            |> Option.iter (fun certificate ->
                let policy =
                    X509ChainPolicy(
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck
                    )

                policy.CustomTrustStore.Add certificate |> ignore
                TlsCertificatePurpose.requireServerAuthentication policy
                options.CertificateChainPolicy <- policy)

            options

    let send
        (configuration: DigestSmtpConfiguration)
        (recipient: string)
        (subject: string)
        (body: string)
        (ct: CancellationToken)
        =
        task {
            use message =
                new MimeMessage(Subject = subject, Body = new TextPart("plain", Text = body))

            message.From.Add(configuration.From.Clone())

            let recipient =
                DigestMailbox.parse recipient
                |> Option.defaultWith (fun () -> raise (FormatException("Invalid digest mailbox.")))

            message.To.Add recipient

            let secure =
                match configuration.Tls with
                | DigestSmtpTls.Implicit -> SecureSocketOptions.SslOnConnect
                | DigestSmtpTls.StartTls -> SecureSocketOptions.StartTls

            use client = new Client(configuration.TrustRoot)
            do! client.ConnectAsync(configuration.Host, configuration.Port, secure, ct)

            match configuration.Credentials with
            | None -> ()
            | Some(user, password) -> do! client.AuthenticateAsync(user, password, ct)

            let! _ = client.SendAsync(SendRequest(message), ct)
            return ()
        }
