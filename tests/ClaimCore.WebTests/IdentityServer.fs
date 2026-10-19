module internal ClaimCore.WebTests.IdentityServer

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers

let private rootCertificate () =
    use key = RSA.Create 2048

    let request =
        CertificateRequest(
            "CN=ClaimCore synthetic identity root",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.CrlSign, true)
    )

    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.), DateTimeOffset.UtcNow.AddDays 1.)

let private serverCertificate (root: X509Certificate2) =
    let key = RSA.Create 2048

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    request.CertificateExtensions.Add(names.Build())
    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))
    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))

    use issued =
        request.Create(
            root,
            DateTimeOffset.UtcNow.AddHours(-1.),
            DateTimeOffset(root.NotAfter.ToUniversalTime()).AddMinutes(-1.),
            RandomNumberGenerator.GetBytes 16
        )

    key, issued.CopyWithPrivateKey key

let private secret () =
    RandomNumberGenerator.GetBytes 24 |> Convert.ToHexStringLower

type Identity() =
    let root = rootCertificate ()
    let key, certificate = serverCertificate root

    let webSecret, serviceSecret, unscopedSecret, password, adminPassword, foreignSecret =
        secret (), secret (), secret (), secret (), secret (), secret ()

    let runId = Guid.NewGuid().ToString "N"

    let image =
        "quay.io/keycloak/keycloak:26.8.0@sha256:b0f60d489d51c5d113390bdf5461d4c06e6051be026c05549f2e1e10ec352bcc"

    let container =
        ContainerBuilder(image)
            .WithName("claimcore-identity-test-" + runId)
            .WithLabel("claimcore.test", "identity")
            .WithLabel("claimcore.test-run", runId)
            .WithCreateParameterModifier(fun parameters ->
                parameters.User <- "0"
                (nonNull parameters.HostConfig).Memory <- 2L * 1024L * 1024L * 1024L)
            .WithTmpfsMount("/opt/keycloak/data/h2")
            .WithPortBinding(8443, true)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "synthetic-admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", adminPassword)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(
                    certificate.ExportCertificatePem() + root.ExportCertificatePem()
                ),
                "/opt/keycloak/conf/server.crt",
                0u,
                0u,
                UnixFileModes.UserRead
            )
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()),
                "/opt/keycloak/conf/server.key",
                0u,
                0u,
                UnixFileModes.UserRead
            )
            .WithResourceMapping(
                IdentityRealm.bytes "synthetic" webSecret serviceSecret unscopedSecret password,
                "/opt/keycloak/data/import/realm.json"
            )
            .WithResourceMapping(
                IdentityRealm.bytes "foreign" webSecret foreignSecret unscopedSecret password,
                "/opt/keycloak/data/import/foreign.json"
            )
            .WithCommand(
                "start-dev",
                "--http-enabled=false",
                "--hostname-strict=false",
                "--https-certificate-file=/opt/keycloak/conf/server.crt",
                "--https-certificate-key-file=/opt/keycloak/conf/server.key",
                "--import-realm",
                "--log-level=warn"
            )
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8443))
            .Build()

    member _.Root = root

    member _.Issuer =
        "https://localhost:"
        + string (container.GetMappedPublicPort 8443)
        + "/realms/synthetic"

    member _.Credentials =
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
            {|
                webClientId = "web"
                webClientSecret = webSecret
                apiAudience = "api"
                serviceClientId = "service"
                serviceClientSecret = serviceSecret
                secondServiceClientId = "second-service"
                publicClientId = "cli"
                unscopedClientId = "unscoped"
                unscopedClientSecret = unscopedSecret
                adminUsername = "synthetic-admin"
                adminPassword = adminPassword
                foreignRealm = "foreign"
                foreignClientSecret = foreignSecret
                users =
                    [|
                        {|
                            username = "synthetic-human"
                            password = password
                        |}
                    |]
            |}

    member this.Start(token: CancellationToken) =
        task {
            do! container.StartAsync token
            use handler = ClaimCore.Web.AuthMiddleware.syntheticTrustHandler root

            use client =
                new HttpClient(
                    handler,
                    false,
                    Timeout = TimeSpan.FromSeconds 5.,
                    MaxResponseContentBufferSize = 65536L
                )

            do! IdentityReadiness.wait client (Uri this.Issuer) token
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        do! container.DisposeAsync().AsTask()
                    finally
                        certificate.Dispose()
                        key.Dispose()
                        root.Dispose()
                }
            )
