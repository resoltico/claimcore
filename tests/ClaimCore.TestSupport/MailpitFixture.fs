module internal ClaimCore.TestSupport.MailpitFixture

open System
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading.Tasks
open ClaimCore.Server
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations

let private request (name: string) (key: RSA) =
    CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

let private rootCertificate () =
    use key = RSA.Create 2048
    let root = request "CN=ClaimCore synthetic Mailpit root" key
    root.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
    root.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true))
    root.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1.), DateTimeOffset.UtcNow.AddDays 1.)

let private serverCertificate (root: X509Certificate2) (key: RSA) =
    let server = request "CN=localhost" key
    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    names.AddIpAddress IPAddress.Loopback
    server.CertificateExtensions.Add(names.Build())
    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    server.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))

    server.Create(
        root,
        DateTimeOffset.UtcNow.AddMinutes(-5.),
        DateTimeOffset.UtcNow.AddHours 1.,
        RandomNumberGenerator.GetBytes 16
    )

type Mailpit(tls: DigestSmtpTls, allowedRecipients: string option) =
    let root = rootCertificate ()
    let smtpPort = 1025
    let httpPort = 8025

    let container =
        use key = RSA.Create 2048
        use certificate = serverCertificate root key

        let tlsRequirement =
            match tls with
            | DigestSmtpTls.StartTls -> "MP_SMTP_REQUIRE_STARTTLS"
            | DigestSmtpTls.Implicit -> "MP_SMTP_REQUIRE_TLS"

        let builder =
            ContainerBuilder(
                "axllent/mailpit:v1.34.0@sha256:23a2ef85bc50853ab3093645d1d64679afd8887e89da5e48a88940c172591a4e"
            )
                .WithLabel("claimcore.test", "mailpit")
                .WithPortBinding(smtpPort, true)
                .WithPortBinding(httpPort, true)
                .WithEnvironment("MP_SMTP_TLS_CERT", "/server.pem")
                .WithEnvironment("MP_SMTP_TLS_KEY", "/server.key")
                .WithEnvironment(tlsRequirement, "true")
                .WithEnvironment("MP_DISABLE_VERSION_CHECK", "true")
                .WithResourceMapping(
                    Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()),
                    FilePath.Of "/server.pem",
                    0u,
                    0u,
                    UnixFileModes.UserRead
                )
                .WithResourceMapping(
                    Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()),
                    FilePath.Of "/server.key",
                    0u,
                    0u,
                    UnixFileModes.UserRead
                )
                .WithWaitStrategy(
                    Wait
                        .ForUnixContainer()
                        .UntilInternalTcpPortIsAvailable(smtpPort)
                        .UntilInternalTcpPortIsAvailable(httpPort)
                )

        let configured =
            match allowedRecipients with
            | None -> builder
            | Some pattern -> builder.WithEnvironment("MP_SMTP_ALLOWED_RECIPIENTS", pattern)

        configured.Build()

    member _.Smtp =
        {
            Host = "127.0.0.1"
            Port = int (container.GetMappedPublicPort smtpPort)
            Tls = tls
            Credentials = None
            TrustRoot = Some root
            From = MimeKit.MailboxAddress("", "digest@example.test")
        }

    member _.Api =
        Uri($"http://127.0.0.1:{container.GetMappedPublicPort httpPort}/api/v1/")

    member _.Start(ct) = container.StartAsync ct

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        do! container.DisposeAsync().AsTask()
                    finally
                        root.Dispose()
                }
            )
