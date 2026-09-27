module ClaimCore.WebTests.OidcConfigurationTests

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open ClaimCore.Web
open ClaimCore.WebTests.ConfigurationTests

let private oidcConfigurationTests () =
    configured (fun directory _ _ ->
        if not (OperatingSystem.IsWindows()) then
            let secret = Path.Combine(directory, "oidc-client-secret")
            File.WriteAllText(secret, "synthetic-secret")
            File.SetUnixFileMode(secret, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

            for name, value in
                [
                    "CLAIMCORE_OIDC_CLIENT_ID", "claimcore-web"
                    "CLAIMCORE_OIDC_CLIENT_SECRET_FILE", secret
                    "CLAIMCORE_OIDC_API_AUDIENCE", "claimcore-api"
                    "CLAIMCORE_OIDC_SERVICE_CLIENT_ID", "claimcore-service"
                    "CLAIMCORE_OIDC_CLI_CLIENT_ID", "claimcore-cli"
                ] do
                Environment.SetEnvironmentVariable(name, value)

            Environment.SetEnvironmentVariable(
                "CLAIMCORE_OIDC_ISSUER",
                "http://127.0.0.1:8080/realms/synthetic"
            )

            Expect.throws
                (fun () -> Configuration.load () |> ignore)
                "Production configuration refuses loopback HTTP issuer"

            Environment.SetEnvironmentVariable(
                "CLAIMCORE_OIDC_ISSUER",
                "https://issuer.example.test/realms/synthetic"
            )

            let loaded = Configuration.load ()
            use _certificate = loaded.Certificate
            Expect.isSome loaded.Oidc "Complete HTTPS OIDC configuration is admitted")

let private configurePrivateCa directory =
    let secret = Path.Combine(directory, "oidc-client-secret")
    File.WriteAllText(secret, "synthetic-secret")
    File.SetUnixFileMode(secret, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    let caPath = Path.Combine(directory, "oidc-ca.pem")
    File.WriteAllText(caPath, "not-a-certificate")
    File.SetUnixFileMode(caPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    for name, value in
        [
            "CLAIMCORE_OIDC_ISSUER", "https://127.0.0.1:9443/realms/synthetic"
            "CLAIMCORE_OIDC_CLIENT_ID", "claimcore-web"
            "CLAIMCORE_OIDC_CLIENT_SECRET_FILE", secret
            "CLAIMCORE_OIDC_API_AUDIENCE", "claimcore-api"
            "CLAIMCORE_OIDC_SERVICE_CLIENT_ID", "claimcore-service"
            "CLAIMCORE_OIDC_CLI_CLIENT_ID", "claimcore-cli"
            "CLAIMCORE_OIDC_CA_CERT_FILE", caPath
        ] do
        Environment.SetEnvironmentVariable(name, value)

    Expect.throws (fun () -> Configuration.load () |> ignore) "Malformed CA is refused"
    caPath

let private writeValidCa (caPath: string) =
    use rsa = RSA.Create(2048)

    let request =
        CertificateRequest(
            "CN=synthetic-ca",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.CrlSign, true)
    )

    use certificate =
        request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1.),
            DateTimeOffset.UtcNow.AddDays(1.)
        )

    File.WriteAllText(caPath, certificate.ExportCertificatePem())
    File.SetUnixFileMode(caPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private assertPrivateCaPolicy (caPath: string) =
    let loaded = Configuration.load ()
    use _certificate = loaded.Certificate
    use _root = loaded.Oidc |> Option.bind _.TrustRoot |> Option.get

    Environment.SetEnvironmentVariable(
        "CLAIMCORE_OIDC_ISSUER",
        "https://issuer.example.test/realms/synthetic"
    )

    Expect.throws
        (fun () -> Configuration.load () |> ignore)
        "A non-loopback IdP cannot use private trust without revocation checking"

    Environment.SetEnvironmentVariable(
        "CLAIMCORE_OIDC_ISSUER",
        "https://127.0.0.1:9443/realms/synthetic"
    )

    File.SetUnixFileMode(caPath, UnixFileMode.UserRead ||| UnixFileMode.GroupRead)
    Expect.throws (fun () -> Configuration.load () |> ignore) "Broad CA file is refused"

let private privateCaPolicy () =
    configured (fun directory _ _ ->
        if not (OperatingSystem.IsWindows()) then
            let caPath = configurePrivateCa directory
            writeValidCa caPath
            assertPrivateCaPolicy caPath)

let tests =
    testList
        "OIDC configuration"
        [
            testCase "[CC-WEB-001] OIDC configuration rejects HTTP issuer" oidcConfigurationTests
            testCase
                "[CC-WEB-001] private CA rejects malformed and broad-permission files"
                privateCaPolicy
        ]
