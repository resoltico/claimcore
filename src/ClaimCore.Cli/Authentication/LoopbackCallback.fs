namespace ClaimCore.Cli

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open ClaimCore.Presentation
open System.Threading
open System.Threading.Tasks
open Duende.IdentityModel.OidcClient.Browser

module internal LoopbackTransport =
    let launchWith executable (address: string) (cancelled: CancellationToken) =
        task {
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

                match Process.Start start with
                | null -> return Error AccessFailure.OidcBrowserUnavailable
                | child ->
                    use child = child

                    let stdout =
                        child.StandardOutput.BaseStream.CopyToAsync(IO.Stream.Null, cancelled)

                    let stderr =
                        child.StandardError.BaseStream.CopyToAsync(IO.Stream.Null, cancelled)

                    do! child.WaitForExitAsync cancelled
                    do! Task.WhenAll(stdout, stderr)

                    return
                        if child.ExitCode = 0 then
                            Ok()
                        else
                            Error AccessFailure.OidcBrowserUnavailable
            with
            | :? ComponentModel.Win32Exception
            | :? InvalidOperationException -> return Error AccessFailure.OidcBrowserUnavailable
        }

    let launch address cancelled =
        if OperatingSystem.IsMacOS() then
            launchWith "open" address cancelled
        elif OperatingSystem.IsLinux() then
            launchWith "xdg-open" address cancelled
        else
            Task.FromResult(Error AccessFailure.OidcBrowserUnavailable)

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

    let private style =
        "html{color-scheme:light dark;font:1rem system-ui}body{display:grid;place-items:center;min-block-size:95vh}main{max-inline-size:28rem;padding:2rem;border:1px solid GrayText;border-radius:.75rem}h1{margin-block-start:0;font-size:1.25rem}"

    let private styleHash =
        style |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToBase64String

    let response language status message =
        let direction = (LocalizationPolicy.languageOption language).direction

        let body =
            $"""<!doctype html><html lang="{WebUtility.HtmlEncode language}" dir="{direction}"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>ClaimCore</title><style>{style}</style><main><h1>ClaimCore</h1><p>{WebUtility.HtmlEncode message}</p></main></html>"""

        let bytes = Encoding.UTF8.GetBytes body

        let header =
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; style-src 'sha256-{styleHash}'; base-uri 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
            )

        Array.append header bytes

    let callbackResponse language accepted =
        let status, identity =
            if accepted then
                "200 OK", CliMessage.CallbackAccepted
            else
                "400 Bad Request", CliMessage.CallbackRefused

        response language status (MessageCatalogs.renderCli language identity)

    let respond language (stream: NetworkStream) accepted cancelled =
        task {
            let bytes = callbackResponse language accepted
            do! stream.WriteAsync(bytes.AsMemory(), cancelled)
            do! stream.FlushAsync(cancelled)
        }

    let readCallback language (listener: TcpListener) host cancelled =
        task {
            let mutable result = None

            while result.IsNone do
                let! client = listener.AcceptTcpClientAsync(cancelled)
                let mutable transferred = false

                try
                    let stream = client.GetStream()
                    let! request = readConnection stream cancelled

                    let callback =
                        match request with
                        | Ok None -> Ok None
                        | Ok(Some source) -> parseRequest host source |> Result.map Some
                        | Error reason -> Error reason

                    match callback with
                    | Ok None -> ()
                    | Ok(Some callback) ->
                        result <- Some(Ok(callback, client))
                        transferred <- true
                    | Error reason ->
                        do! respond language stream false cancelled
                        result <- Some(Error reason)
                finally
                    if not transferred then
                        client.Dispose()

            return result.Value
        }

/// One IPv4 loopback callback. OAuth state, issuer and PKCE validation belong to OidcClient.
type internal LoopbackBrowser
    (language, launch: string -> CancellationToken -> Task<Result<unit, AccessFailure>>, showAddress)
    =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let mutable callbackClient: TcpClient option = None
    let mutable invoked = 0
    let mutable failure: AccessFailure option = None
    do listener.Start 1
    let host = (listener.LocalEndpoint :?> IPEndPoint).ToString()

    let receive (cancelled: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource cancelled
            deadline.CancelAfter(TimeSpan.FromMinutes 2.)

            try
                let! parsed = LoopbackTransport.readCallback language listener host deadline.Token

                match parsed with
                | Ok(response, client) ->
                    callbackClient <- Some client

                    return
                        BrowserResult(ResultType = BrowserResultType.Success, Response = response)
                | Error reason ->
                    failure <- Some reason
                    return BrowserResult(ResultType = BrowserResultType.HttpError)
            with
            | :? OperationCanceledException ->
                if not cancelled.IsCancellationRequested then
                    failure <- Some AccessFailure.OidcInteractionTimeout

                return BrowserResult(ResultType = BrowserResultType.Timeout)
            | :? IO.IOException
            | :? SocketException ->
                failure <- Some AccessFailure.OidcCallbackInvalid
                return BrowserResult(ResultType = BrowserResultType.HttpError)
        }

    new() = new LoopbackBrowser("en", LoopbackTransport.launch, ignore)

    member _.RedirectUri = "http://" + host
    member _.Failure = failure

    member _.Complete(accepted) =
        task {
            match callbackClient with
            | None -> ()
            | Some client ->
                callbackClient <- None
                use client = client
                use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 2.)

                try
                    do!
                        LoopbackTransport.respond
                            language
                            (client.GetStream())
                            accepted
                            deadline.Token
                with
                | :? IO.IOException
                | :? SocketException
                | :? OperationCanceledException -> ()
        }

    interface IBrowser with
        member _.InvokeAsync(options, cancelled) =
            task {
                if Interlocked.Exchange(&invoked, 1) <> 0 then
                    return BrowserResult(ResultType = BrowserResultType.UnknownError)
                else
                    use launchCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource cancelled

                    let launching =
                        task {
                            try
                                match! launch options.StartUrl launchCancellation.Token with
                                | Ok() -> ()
                                | Error _ when not launchCancellation.IsCancellationRequested ->
                                    showAddress options.StartUrl
                                | Error _ -> ()
                            with :? OperationCanceledException when
                                launchCancellation.IsCancellationRequested ->
                                ()
                        }

                    let! result = receive cancelled
                    launchCancellation.Cancel()
                    do! launching
                    return result
            }

    interface IDisposable with
        member _.Dispose() =
            callbackClient |> Option.iter _.Dispose()
            listener.Stop()
