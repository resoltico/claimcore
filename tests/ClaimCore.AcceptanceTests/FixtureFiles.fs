module internal ClaimCore.AcceptanceTests.FixtureFiles

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates

let directory () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let path =
        Path.Combine(physical, "claimcore-acceptance-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    path

let write directory name (bytes: byte array) =
    let path = Path.Combine(directory, name)
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path

let text directory name (value: string) =
    write directory name (Text.Encoding.UTF8.GetBytes value)

let port () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let certificate () =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.DigitalSignature,
            true
        )
    )

    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))
    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    request.CertificateExtensions.Add(names.Build())
    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1.), DateTimeOffset.UtcNow.AddDays(1.))

/// A replacement leaf preserves the owned trust anchor and localhost identity.
let serverCertificate (issuer: X509Certificate2) =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
    )

    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))
    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName "localhost"
    request.CertificateExtensions.Add(names.Build())

    use publicCertificate =
        request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1.),
            DateTimeOffset.UtcNow.AddHours(12.),
            RandomNumberGenerator.GetBytes 16
        )

    publicCertificate.CopyWithPrivateKey key
