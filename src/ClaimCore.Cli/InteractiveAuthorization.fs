namespace ClaimCore.Cli

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading

module InteractiveAuthorization =
    let private launch (authorization: Uri) =
        let executable =
            if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
                Some "open"
            elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
                Some "xdg-open"
            else
                None

        match executable with
        | None -> Error "OIDC_BROWSER_UNAVAILABLE"
        | Some name ->
            try
                let start = ProcessStartInfo(name)
                start.UseShellExecute <- false
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                start.CreateNoWindow <- true
                start.ArgumentList.Add(authorization.AbsoluteUri)

                use child = Process.Start(start)

                if isNull child then
                    Error "OIDC_BROWSER_UNAVAILABLE"
                else
                    Ok()
            with
            | :? ComponentModel.Win32Exception
            | :? InvalidOperationException -> Error "OIDC_BROWSER_UNAVAILABLE"

    let obtain
        (endpoints: OidcEndpoints)
        (issuer: Uri)
        (clientId: string)
        (cancelled: CancellationToken)
        =
        task {
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start(1)
            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
            let redirect = Uri($"http://127.0.0.1:{port}/")
            let pkce = OidcClient.createPkceSession ()

            match OidcClient.authorizationUri endpoints clientId redirect pkce with
            | Error reason -> return Error reason
            | Ok authorization ->
                match launch authorization with
                | Error reason -> return Error reason
                | Ok() ->
                    match!
                        LoopbackCallback.receive listener issuer.AbsoluteUri pkce.State cancelled
                    with
                    | Error reason -> return Error reason
                    | Ok code -> return Ok(code, redirect, pkce.Verifier)
        }
