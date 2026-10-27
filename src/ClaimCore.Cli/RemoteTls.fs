namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open System
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text
open ClaimCore.HostSecurity

/// System trust is the default. An explicit owner-private PEM root replaces it only for a
/// local synthetic endpoint. Hostname matching remains mandatory. The isolated custom-root
/// fixture has no CRL, so its chain uses NoCheck; this is not a remote production trust mode.
type RemoteTls private (handler: HttpClientHandler, root: X509Certificate2 option) =
    member _.Handler = handler

    interface IDisposable with
        member _.Dispose() =
            handler.Dispose()
            root |> Option.iter _.Dispose()

    static member private LocalRoot(bytes: byte array) =
        let certificate =
            X509Certificate2.CreateFromPem(UTF8Encoding(false, true).GetString(bytes).AsSpan())

        if not (TlsCertificatePurpose.validRoot certificate DateTime.UtcNow) then
            certificate.Dispose()
            invalidOp "Private TLS root is invalid."

        let handler = new HttpClientHandler(AllowAutoRedirect = false)

        handler.ServerCertificateCustomValidationCallback <-
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>
                (fun _ presented _ errors ->
                    if
                        (errors &&& SslPolicyErrors.RemoteCertificateNameMismatch)
                        <> SslPolicyErrors.None
                        || (errors &&& SslPolicyErrors.RemoteCertificateNotAvailable)
                           <> SslPolicyErrors.None
                    then
                        false
                    else
                        TlsCertificatePurpose.customRootServerAuthentication certificate presented)

        new RemoteTls(handler, Some certificate)

    static member Create(path: string option, endpoint: Uri) =
        match path with
        | None ->
            Ok(
                new RemoteTls(
                    new HttpClientHandler(
                        AllowAutoRedirect = false,
                        CheckCertificateRevocationList = true
                    ),
                    None
                )
            )
        | Some _ when not (HttpsOrigins.isLocal endpoint) ->
            Error AccessFailure.TlsTrustRootScopeInvalid
        | Some location ->
            match PrivateFile.read CliConfigurationLimits.MaximumTrustRootBytes location with
            | Error _ -> Error AccessFailure.TlsTrustRootUnavailable
            | Ok bytes ->
                try
                    try
                        Ok(RemoteTls.LocalRoot bytes)
                    with
                    | :? System.Security.Cryptography.CryptographicException
                    | :? ArgumentException
                    | :? InvalidOperationException -> Error AccessFailure.TlsTrustRootInvalid
                finally
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory bytes
