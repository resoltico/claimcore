module ClaimCore.WebTests.AuthTrustTests

open System
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open ClaimCore.Web

let private authority commonName =
    let key = RSA.Create(2048)

    let request =
        CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.CrlSign, true)
    )

    request.CertificateExtensions.Add(X509SubjectKeyIdentifierExtension(request.PublicKey, false))
    let now = DateTimeOffset.UtcNow
    key, request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1))

let private serverCertificate (root: X509Certificate2) =
    use key = RSA.Create(2048)

    let request =
        CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))

    request.CertificateExtensions.Add(
        X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
    )

    let names = SubjectAlternativeNameBuilder()
    names.AddDnsName("localhost")
    request.CertificateExtensions.Add(names.Build())
    let purposes = OidCollection()
    purposes.Add(Oid("1.3.6.1.5.5.7.3.1")) |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(purposes, false))
    let now = DateTimeOffset.UtcNow
    let serial = RandomNumberGenerator.GetBytes(16)
    request.Create(root, now.AddHours(-1), now.AddHours(1), serial)

let private syntheticIssuerTrust () =
    let rootKey, rootCertificate = authority "synthetic-issuer-root"
    let otherKey, otherCertificate = authority "unrelated-root"
    use _rootKey = rootKey
    use _otherKey = otherKey
    use root = rootCertificate
    use otherRoot = otherCertificate
    use server = serverCertificate root
    use request = new HttpRequestMessage(HttpMethod.Get, "https://localhost")
    use trusted = AuthMiddleware.syntheticTrustHandler root
    use unrelated = AuthMiddleware.syntheticTrustHandler otherRoot
    let accepts = trusted.ServerCertificateCustomValidationCallback
    let rejects = unrelated.ServerCertificateCustomValidationCallback

    Expect.isFalse
        (accepts.Invoke(request, server, null, SslPolicyErrors.RemoteCertificateNotAvailable))
        "An absent peer certificate is never accepted"

    Expect.isFalse
        (accepts.Invoke(request, server, null, SslPolicyErrors.RemoteCertificateNameMismatch))
        "A wrong DNS identity is never accepted"

    Expect.isFalse
        (rejects.Invoke(request, server, null, SslPolicyErrors.None))
        "A certificate outside the pinned synthetic root is rejected"

    Expect.isTrue
        (accepts.Invoke(request, server, null, SslPolicyErrors.RemoteCertificateChainErrors))
        "The exact pinned root rebuilds and authenticates the certificate chain"

let tests =
    testList
        "Web synthetic issuer transport trust"
        [
            testCase
                "[CC-WEB-001] synthetic issuer TLS refuses absent wrong-host and unrelated certificates"
                syntheticIssuerTrust
        ]
