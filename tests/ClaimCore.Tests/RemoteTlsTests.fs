module ClaimCore.Tests.RemoteTlsTests

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open ClaimCore.Cli
open ClaimCore.HostSecurity

let private trustRootScope () =
    match RemoteTls.Create(None, Uri("https://remote.example.test/")) with
    | Error _ -> failtest "System trust must be available for remote HTTPS"
    | Ok client ->
        use _owned = client :> IDisposable

        Expect.isTrue
            client.Handler.CheckCertificateRevocationList
            "Remote HTTPS checks certificate revocation"

    match
        RemoteTls.Create(Some "/private/synthetic-ca.pem", Uri("https://remote.example.test/"))
    with
    | Ok value ->
        (value :> IDisposable).Dispose()
        failtest "A local test root must not authorize a remote production endpoint"
    | Error reason ->
        Expect.equal reason "TLS_TRUST_ROOT_SCOPE_INVALID" "No remote custom trust root"

let private leafPurpose () =
    use rsa = RSA.Create(2048)

    let request =
        CertificateRequest(
            "CN=synthetic-leaf",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    let addPurpose (purpose: string) =
        let collection = OidCollection()
        collection.Add(Oid(purpose)) |> ignore
        request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(collection, true))

    addPurpose "1.3.6.1.5.5.7.3.2"

    use clientOnly =
        request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1.),
            DateTimeOffset.UtcNow.AddDays(1.)
        )

    Expect.isFalse
        (TlsCertificatePurpose.serverAuthentication clientOnly)
        "A client-auth-only leaf cannot authenticate the issuer server"

let private rootValidityBoundaries () =
    use rsa = RSA.Create(2048)

    let request =
        CertificateRequest(
            "CN=synthetic-root",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )

    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
    request.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true))
    let first = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let last = first.AddDays(1.)
    use signed = request.CreateSelfSigned(first, last)

    use publicOnly =
        X509CertificateLoader.LoadCertificate(signed.Export(X509ContentType.Cert))

    let valid instant =
        TlsCertificatePurpose.validRoot publicOnly instant

    Expect.isFalse (valid (first.UtcDateTime.AddTicks(-1L))) "Not yet valid root refuses."
    Expect.isTrue (valid first.UtcDateTime) "Validity starts inclusively."
    Expect.isTrue (valid (last.UtcDateTime.AddTicks(-1L))) "Root remains valid before expiry."
    Expect.isFalse (valid last.UtcDateTime) "Exact expiry refuses."

    Expect.isFalse
        (TlsCertificatePurpose.validRoot signed first.UtcDateTime)
        "Private roots refuse."

let tests =
    testList
        "CLI TLS trust"
        [
            testCase
                "[CC-WEB-001] private TLS root validity has exact boundaries"
                rootValidityBoundaries
            testCase "private test trust root is confined to loopback" trustRootScope
            testCase "[CC-WEB-001] issuer TLS leaf requires server purpose" leafPurpose
        ]
