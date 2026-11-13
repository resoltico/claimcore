namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.Server.Composition
open ClaimCore.HostSecurity

/// An optional owner-private trust root for a local HTTPS qualification issuer only.
/// Hostname validation remains mandatory. Remote IdPs use system trust and revocation policy.
module internal OidcTrustRoot =
    let load path =
        let invalid () =
            WebStartupDiagnostics.refuse WebStartupProblem.OidcConfigurationInvalid

        let pem =
            match PrivateInputs.text 32768 path with
            | Ok value -> value
            | Error _ -> invalid ()

        let certificate =
            try
                X509Certificate2.CreateFromPem(pem.AsSpan())
            with :? CryptographicException ->
                invalid ()

        if not (TlsCertificatePurpose.validRoot certificate DateTime.UtcNow) then
            certificate.Dispose()
            invalid ()

        certificate
