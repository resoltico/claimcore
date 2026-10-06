namespace ClaimCore.HostSecurity

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates

/// Purpose checks shared by the two loopback-only custom-root HTTPS clients.
module TlsCertificatePurpose =
    let validRoot (certificate: X509Certificate2) (now: DateTime) =
        let basic =
            certificate.Extensions
            |> Seq.tryPick (function
                | :? X509BasicConstraintsExtension as value -> Some value
                | _ -> None)

        let usage =
            certificate.Extensions
            |> Seq.tryPick (function
                | :? X509KeyUsageExtension as value -> Some value
                | _ -> None)

        basic |> Option.exists _.CertificateAuthority
        && (usage
            |> Option.exists (fun value ->
                (value.KeyUsages &&& X509KeyUsageFlags.KeyCertSign) = X509KeyUsageFlags.KeyCertSign))
        && certificate.Subject = certificate.Issuer
        && not certificate.HasPrivateKey
        && certificate.NotBefore.ToUniversalTime() <= now
        && now < certificate.NotAfter.ToUniversalTime()

    let serverAuthentication (certificate: X509Certificate2) =
        certificate.Extensions
        |> Seq.exists (function
            | :? X509EnhancedKeyUsageExtension as value ->
                value.EnhancedKeyUsages
                |> Seq.cast<Oid>
                |> Seq.exists (fun oid -> oid.Value = "1.3.6.1.5.5.7.3.1")
            | _ -> false)

    let requireServerAuthentication (chain: X509Chain) =
        chain.ChainPolicy.ApplicationPolicy.Add(
            System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")
        )
        |> ignore

    /// The caller retains hostname validation and owns the admitted public root.
    let customRootServerAuthentication (root: X509Certificate2) (certificate: X509Certificate2) =
        use chain = new X509Chain()
        chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
        chain.ChainPolicy.CustomTrustStore.Add(root) |> ignore
        chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
        requireServerAuthentication chain
        serverAuthentication certificate && chain.Build(certificate)
