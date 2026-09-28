namespace ClaimCore.Cli

open System
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text
open ClaimCore.HostSecurity

/// System trust is the default. An explicit owner-private PEM root replaces it only for a
/// loopback synthetic endpoint. Hostname matching remains mandatory. The isolated custom-root
/// fixture has no CRL, so its chain uses NoCheck; this is not a remote production trust mode.
type RemoteTls private (handler: HttpClientHandler, root: X509Certificate2 option) =
    member _.Handler = handler

    interface IDisposable with
        member _.Dispose() =
            handler.Dispose()
            root |> Option.iter _.Dispose()

    static member Create(path: string option, endpoint: Uri) =
        match path with
        | None -> Ok(new RemoteTls(new HttpClientHandler(AllowAutoRedirect = false), None))
        | Some _ when not endpoint.IsLoopback -> Error "TLS_TRUST_ROOT_SCOPE_INVALID"
        | Some location ->
            match PrivateFileService.readUtf8Bytes 16384 location with
            | Error _ -> Error "TLS_TRUST_ROOT_UNAVAILABLE"
            | Ok bytes ->
                try
                    let certificate =
                        X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(bytes).AsSpan())

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
                                    use chain = new X509Chain()

                                    chain.ChainPolicy.TrustMode <-
                                        X509ChainTrustMode.CustomRootTrust

                                    chain.ChainPolicy.CustomTrustStore.Add(certificate) |> ignore
                                    chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
                                    chain.Build(presented))

                    Ok(new RemoteTls(handler, Some certificate))
                with
                | :? System.Security.Cryptography.CryptographicException
                | :? ArgumentException -> Error "TLS_TRUST_ROOT_INVALID"
