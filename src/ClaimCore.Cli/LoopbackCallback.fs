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
        | None -> Error AccessFailure.OidcBrowserUnavailable
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
                    Error AccessFailure.OidcBrowserUnavailable
                else
                    Ok()
            with
            | :? ComponentModel.Win32Exception
            | :? InvalidOperationException -> Error AccessFailure.OidcBrowserUnavailable

    let private callbackUri host (target: string) =
        if
            not (target.StartsWith("/", StringComparison.Ordinal))
            || target.StartsWith("//", StringComparison.Ordinal)
        then
            Error AccessFailure.OidcCallbackInvalid
        else
            match Uri.TryCreate("http://" + host + target, UriKind.Absolute) with
            | true, uri ->
                match Option.ofObj uri with
                | Some uri when uri.AbsolutePath = "/" && uri.Fragment = "" -> Ok uri.AbsoluteUri
                | _ -> Error AccessFailure.OidcCallbackInvalid
            | _ -> Error AccessFailure.OidcCallbackInvalid

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
        | _ -> Error AccessFailure.OidcCallbackInvalid

    let readHeaders (stream: NetworkStream) cancelled =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let one = Array.zeroCreate<byte> 1
            let mutable count = 0
            let mutable complete = false
            let mutable reading = true

            try
                while reading && not complete && count < buffer.Length do
                    let! received = stream.ReadAsync(one.AsMemory(), cancelled)

                    if received = 0 then
                        reading <- false
                    else
                        buffer[count] <- one[0]
                        count <- count + 1

                        complete <-
                            count >= 4
                            && buffer[count - 4 .. count - 1] = [| 13uy; 10uy; 13uy; 10uy |]
            with
            | :? IO.IOException when count = 0 -> ()
            | :? OperationCanceledException when count > 0 -> ()

            if
                complete
                && count < buffer.Length
                && (buffer[.. count - 1]
                    |> Array.forall (fun c -> c = 13uy || c = 10uy || (c >= 32uy && c <= 126uy)))
            then
                return Ok(Some(Encoding.ASCII.GetString(buffer, 0, count)))
            elif count = 0 then
                return Ok None
            else
                return Error AccessFailure.OidcCallbackInvalid
        }

    let readConnection (stream: NetworkStream) (cancelled: CancellationToken) =
        task {
            // A browser's idle preconnection must not consume the one callback.
            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancelled
            deadline.CancelAfter(TimeSpan.FromSeconds 2.)

            try
                let! result = readHeaders stream deadline.Token
                cancelled.ThrowIfCancellationRequested()
                return result
            with :? OperationCanceledException when not cancelled.IsCancellationRequested ->
                return Ok None
        }

    let response status body =
        let bytes = Encoding.UTF8.GetBytes(body: string)

        let header =
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
            )

        Array.append header bytes

    let callbackResponse accepted =
        let status, identity =
            if accepted then
                "200 OK", CliMessage.CallbackAccepted
            else
                "400 Bad Request", CliMessage.CallbackRefused

        response status (ContractDiscovery.message (CliMessages.key identity))

    let respond (stream: NetworkStream) accepted cancelled =
        task {
            let bytes = callbackResponse accepted
            do! stream.WriteAsync(bytes.AsMemory(), cancelled)
            do! stream.FlushAsync(cancelled)
        }

    let readCallback (listener: TcpListener) host cancelled =
        task {
            let mutable result = None

            while result.IsNone do
                use! client = listener.AcceptTcpClientAsync(cancelled)
                use stream = client.GetStream()
                let! request = readConnection stream cancelled

                let parsed =
                    match request with
                    | Ok None -> None
                    | Ok(Some source) -> Some(parseRequest host source)
                    | Error reason -> Some(Error reason)

                match parsed with
                | None -> ()
                | Some callback ->
                    do! respond stream (Result.isOk callback) cancelled
                    result <- Some callback

            return result.Value
        }

/// One IPv4 loopback callback. OAuth state, issuer and PKCE validation belong to OidcClient.
type internal LoopbackBrowser(launch: string -> Result<unit, AccessFailure>) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let mutable invoked = 0
    let mutable failure: AccessFailure option = None
    do listener.Start 1
    let host = (listener.LocalEndpoint :?> IPEndPoint).ToString()

    let receive (cancelled: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancelled
            deadline.CancelAfter(TimeSpan.FromMinutes 2.)

            try
                let! parsed = LoopbackTransport.readCallback listener host deadline.Token

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
                failure <- Some AccessFailure.OidcCallbackInvalid
                return BrowserResult(ResultType = BrowserResultType.HttpError)
        }

    new() = new LoopbackBrowser(LoopbackTransport.launch)

    member _.RedirectUri = "http://" + host
    member _.Failure = failure

    interface IBrowser with
        member _.InvokeAsync(options, cancelled) =
            task {
                if Interlocked.Exchange(&invoked, 1) <> 0 then
                    return BrowserResult(ResultType = BrowserResultType.UnknownError)
                else
                    match launch options.StartUrl with
                    | Ok() -> return! receive cancelled
                    | Error reason ->
                        failure <- Some reason
                        return BrowserResult(ResultType = BrowserResultType.UnknownError)
            }

    interface IDisposable with
        member _.Dispose() = listener.Stop()
