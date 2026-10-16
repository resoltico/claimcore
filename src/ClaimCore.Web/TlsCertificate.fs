namespace ClaimCore.Web

open ClaimCore.ClientProtocol

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.Hosting
open ClaimCore.HostSecurity

module internal TlsCertificate =
    let private certificateBytes path =
        match PrivateFile.read (8 * 1024 * 1024) path with
        | Ok value when value.Length > 0 -> value
        | Error PrivateFileFailure.NativeAssetUnavailable ->
            Console.Error.WriteLine PrivateFile.nativeAssetGuidance
            WebStartupDiagnostics.refuse WebStartupProblem.CertificateAccessRefused
        | Ok _
        | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.CertificateAccessRefused

    let load hostname path =
        let bytes = certificateBytes path

        // macOS does not implement EphemeralKeySet for PKCS#12 imports. The certificate is a
        // private, mode-restricted local deployment input; use the platform default there and
        // retain ephemeral key storage everywhere it is supported.
        let keyStorage =
            if OperatingSystem.IsMacOS() then
                X509KeyStorageFlags.DefaultKeySet
            else
                X509KeyStorageFlags.EphemeralKeySet

        let loaded =
            try
                try
                    X509CertificateLoader.LoadPkcs12(
                        ReadOnlySpan<byte>(bytes),
                        ReadOnlySpan<char>.Empty,
                        keyStorage
                    )
                with :? CryptographicException ->
                    WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))

        try
            if not loaded.HasPrivateKey then
                WebStartupDiagnostics.refuse WebStartupProblem.CertificateKeyMissing

            let usable =
                try
                    let now = DateTime.UtcNow

                    let serverAuthentication =
                        loaded.Extensions
                        |> Seq.tryPick (function
                            | :? X509EnhancedKeyUsageExtension as value -> Some value
                            | _ -> None)
                        |> Option.exists (fun value ->
                            value.EnhancedKeyUsages
                            |> Seq.cast<Oid>
                            |> Seq.exists (fun usage -> usage.Value = "1.3.6.1.5.5.7.3.1"))

                    loaded.NotBefore.ToUniversalTime() <= now
                    && now < loaded.NotAfter.ToUniversalTime()
                    && loaded.MatchesHostname(hostname, true, false)
                    && serverAuthentication
                with :? CryptographicException ->
                    false

            if not usable then
                WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid

            loaded
        with _ ->
            loaded.Dispose()
            reraise ()
