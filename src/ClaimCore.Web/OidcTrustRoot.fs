namespace ClaimCore.Web

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.Contracts
open ClaimCore.HostSecurity

/// An optional owner-private trust root for a loopback HTTPS qualification issuer only.
/// Hostname validation remains mandatory. Remote IdPs use system trust and revocation policy.
module internal OidcTrustRoot =
    let load path =
        let invalid () =
            WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

        let pem =
            match PrivateFileService.readUtf8Text 32768 path with
            | Ok value -> value
            | Error _ -> invalid ()

        let certificate =
            try
                X509Certificate2.CreateFromPem(pem.AsSpan())
            with :? CryptographicException ->
                invalid ()

        let basic =
            certificate.Extensions
            |> Seq.tryPick (function
                | :? X509BasicConstraintsExtension as extension -> Some extension
                | _ -> None)

        let keyUsage =
            certificate.Extensions
            |> Seq.tryPick (function
                | :? X509KeyUsageExtension as extension -> Some extension
                | _ -> None)

        let now = DateTime.UtcNow

        let usable =
            basic |> Option.exists _.CertificateAuthority
            && (keyUsage
                |> Option.exists (fun value ->
                    (value.KeyUsages &&& X509KeyUsageFlags.KeyCertSign) =
                        X509KeyUsageFlags.KeyCertSign))
            && certificate.Subject = certificate.Issuer
            && not certificate.HasPrivateKey
            && certificate.NotBefore.ToUniversalTime() <= now
            && now < certificate.NotAfter.ToUniversalTime()

        if not usable then
            certificate.Dispose()
            invalid ()

        certificate
