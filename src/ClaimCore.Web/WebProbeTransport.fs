namespace ClaimCore.Web

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks
open ClaimCore.HostSecurity

/// Connect to this listener while TLS SNI and HTTP authority retain the public origin.
module internal WebProbeTransport =
    let private connect (binding: WebBinding) (_: SocketsHttpConnectionContext) ct =
        ValueTask<Stream>(
            task {
                let address =
                    if binding.ListenAddress = IPAddress.Any then
                        IPAddress.Loopback
                    elif binding.ListenAddress = IPAddress.IPv6Any then
                        IPAddress.IPv6Loopback
                    else
                        binding.ListenAddress

                let socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)

                try
                    do! socket.ConnectAsync(IPEndPoint(address, binding.ListenPort), ct)
                    return new NetworkStream(socket, true) :> Stream
                with error ->
                    socket.Dispose()
                    return raise error
            }
        )

    let create binding (root: X509Certificate2 option) =
        let handler = new SocketsHttpHandler(AllowAutoRedirect = false, UseProxy = false)
        handler.ConnectCallback <- Func<_, _, _>(connect binding)
        handler.SslOptions.CertificateRevocationCheckMode <- X509RevocationMode.Online

        root
        |> Option.iter (fun trusted ->
            handler.SslOptions.CertificateRevocationCheckMode <- X509RevocationMode.NoCheck

            handler.SslOptions.RemoteCertificateValidationCallback <-
                RemoteCertificateValidationCallback(fun _ certificate _ errors ->
                    match certificate with
                    | :? X509Certificate2 as presented when
                        (errors &&& SslPolicyErrors.RemoteCertificateNameMismatch) =
                            SslPolicyErrors.None
                        && (errors &&& SslPolicyErrors.RemoteCertificateNotAvailable) =
                            SslPolicyErrors.None
                        ->
                        TlsCertificatePurpose.customRootServerAuthentication trusted presented
                    | _ -> false))

        handler
