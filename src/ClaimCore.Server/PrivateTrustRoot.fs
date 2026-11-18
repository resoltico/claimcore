namespace ClaimCore.Server

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.HostSecurity

module internal PrivateTrustRoot =
    let load fault path =
        let invalid () = WebStartupDiagnostics.refuse fault

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
