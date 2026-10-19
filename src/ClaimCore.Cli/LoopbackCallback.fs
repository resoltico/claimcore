namespace ClaimCore.Cli

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Duende.IdentityModel.OidcClient.Browser

module internal LoopbackTransport =
    let launch (address: string) =
        let executable =
            if OperatingSystem.IsMacOS() then Some "open"
            elif OperatingSystem.IsLinux() then Some "xdg-open"
            else None

        match executable with
        | None -> Error "OIDC_BROWSER_UNAVAILABLE"
        | Some executable ->
            try
                let start =
                    ProcessStartInfo(
                        executable,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    )

                start.ArgumentList.Add address
                use child = Process.Start start

                if isNull child then
                    Error "OIDC_BROWSER_UNAVAILABLE"
                else
                    Ok()
            with
            | :? ComponentModel.Win32Exception
            | :? InvalidOperationException -> Error "OIDC_BROWSER_UNAVAILABLE"

    let private callbackUri host (target: string) =
        if
            not (target.StartsWith("/", StringComparison.Ordinal))
            || target.StartsWith("//", StringComparison.Ordinal)
        then
            Error "OIDC_CALLBACK_INVALID"
        else
            match Uri.TryCreate("http://" + host + target, UriKind.Absolute) with
            | true, uri ->
                match Option.ofObj uri with
                | Some uri when uri.AbsolutePath = "/" && uri.Fragment = "" -> Ok uri.AbsoluteUri
                | _ -> Error "OIDC_CALLBACK_INVALID"
            | _ -> Error "OIDC_CALLBACK_INVALID"

    let parseRequest host (source: string) =
        let lines = source.Split("\r\n", StringSplitOptions.None)

        let hosts =
            lines
            |> Array.choose (fun line ->
                if line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase) then
                    Some(line.Substring(5).Trim())
                else
                    None)

        match lines[0].Split(' ', StringSplitOptions.None) with
        | [| "GET"; target; "HTTP/1.1" |] when hosts = [| host |] -> callbackUri host target
        | _ -> Error "OIDC_CALLBACK_INVALID"

    let readHeaders (stream: NetworkStream) cancelled =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let one = Array.zeroCreate<byte> 1
            let mutable count = 0
            let mutable complete = false
            let mutable reading = true

            while reading && not complete && count < buffer.Length do
                let! received = stream.ReadAsync(one.AsMemory(), cancelled)

                if received = 0 then
                    reading <- false
                else
                    buffer[count] <- one[0]
                    count <- count + 1

                    complete <-
                        count >= 4 && buffer[count - 4 .. count - 1] = [| 13uy; 10uy; 13uy; 10uy |]

            if
                complete
                && count < buffer.Length
                && (buffer[.. count - 1]
                    |> Array.forall (fun c -> c = 13uy || c = 10uy || (c >= 32uy && c <= 126uy)))
            then
                return Ok(Encoding.ASCII.GetString(buffer, 0, count))
            else
                return Error "OIDC_CALLBACK_INVALID"
        }

    let respond (stream: NetworkStream) accepted cancelled =
        task {
            let status = if accepted then "200 OK" else "400 Bad Request"

            let body =
                if accepted then
                    "Authentication response received. Return to the terminal."
                else
                    "Authentication response refused."

            let bytes = Encoding.ASCII.GetBytes body

            let header =
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=us-ascii\r\nCache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
                )

            do! stream.WriteAsync(header.AsMemory(), cancelled)
            do! stream.WriteAsync(bytes.AsMemory(), cancelled)
            do! stream.FlushAsync(cancelled)
        }

/// One IPv4 loopback callback. OAuth state, issuer and PKCE validation belong to OidcClient.
type internal LoopbackBrowser() =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let mutable invoked = 0
    let mutable failure: string option = None
    do listener.Start 1
    let host = (listener.LocalEndpoint :?> IPEndPoint).ToString()

    let receive (cancelled: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancelled
            deadline.CancelAfter(TimeSpan.FromMinutes 2.)

            try
                use! client = listener.AcceptTcpClientAsync(deadline.Token)
                use stream = client.GetStream()
                let! request = LoopbackTransport.readHeaders stream deadline.Token
                let parsed = request |> Result.bind (LoopbackTransport.parseRequest host)
                do! LoopbackTransport.respond stream (Result.isOk parsed) deadline.Token

                match parsed with
                | Ok response ->
                    return
                        BrowserResult(ResultType = BrowserResultType.Success, Response = response)
                | Error reason ->
                    failure <- Some reason
                    return BrowserResult(ResultType = BrowserResultType.HttpError)
            with
            | :? OperationCanceledException ->
                return BrowserResult(ResultType = BrowserResultType.Timeout)
            | :? IO.IOException
            | :? SocketException ->
                failure <- Some "OIDC_CALLBACK_INVALID"
                return BrowserResult(ResultType = BrowserResultType.HttpError)
        }

    member _.RedirectUri = "http://" + host
    member _.Failure = failure

    interface IBrowser with
        member _.InvokeAsync(options, cancelled) =
            task {
                if Interlocked.Exchange(&invoked, 1) <> 0 then
                    return BrowserResult(ResultType = BrowserResultType.UnknownError)
                else
                    match LoopbackTransport.launch options.StartUrl with
                    | Ok() -> return! receive cancelled
                    | Error reason ->
                        failure <- Some reason
                        return BrowserResult(ResultType = BrowserResultType.UnknownError)
            }

    interface IDisposable with
        member _.Dispose() = listener.Stop()
