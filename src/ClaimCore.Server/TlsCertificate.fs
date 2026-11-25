namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open ClaimCore.Server.Composition
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type ServerCertificateBundle =
    {
        Leaf: X509Certificate2
        Chain: X509Certificate2Collection
    }

    member this.Dispose() =
        this.Leaf.Dispose()

        for certificate in this.Chain do
            certificate.Dispose()

module internal TlsCertificate =
    let private certificateBytes path =
        match PrivateFile.read (8 * 1024 * 1024) path with
        | Ok value when value.Length > 0 -> value
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
                    X509CertificateLoader.LoadPkcs12Collection(
                        ReadOnlySpan<byte>(bytes),
                        ReadOnlySpan<char>.Empty,
                        keyStorage
                    )
                with :? CryptographicException ->
                    WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))

        try
            let leaf =
                match loaded |> Seq.filter _.HasPrivateKey |> Seq.toList with
                | [ certificate ] -> certificate
                | [] -> WebStartupDiagnostics.refuse WebStartupProblem.CertificateKeyMissing
                | _ -> WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid

            let usable =
                try
                    let now = DateTime.UtcNow

                    let serverAuthentication =
                        leaf.Extensions
                        |> Seq.tryPick (function
                            | :? X509EnhancedKeyUsageExtension as value -> Some value
                            | _ -> None)
                        |> Option.exists (fun value ->
                            value.EnhancedKeyUsages
                            |> Seq.cast<Oid>
                            |> Seq.exists (fun usage -> usage.Value = "1.3.6.1.5.5.7.3.1"))

                    let authority =
                        leaf.Extensions
                        |> Seq.exists (function
                            | :? X509BasicConstraintsExtension as value ->
                                value.CertificateAuthority
                            | _ -> false)

                    leaf.NotBefore.ToUniversalTime() <= now
                    && now < leaf.NotAfter.ToUniversalTime()
                    && leaf.MatchesHostname(hostname, true, false)
                    && serverAuthentication
                    && not authority
                with :? CryptographicException ->
                    false

            if not usable then
                WebStartupDiagnostics.refuse WebStartupProblem.CertificateInvalid

            let chain = X509Certificate2Collection()

            for certificate in loaded do
                if not (Object.ReferenceEquals(certificate, leaf)) then
                    chain.Add certificate |> ignore

            { Leaf = leaf; Chain = chain }
        with _ ->
            for certificate in loaded do
                certificate.Dispose()

            reraise ()
