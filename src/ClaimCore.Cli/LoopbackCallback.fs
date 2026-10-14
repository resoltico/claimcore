namespace ClaimCore.Cli

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading

module LoopbackCallback =
    let private pairs (query: string) =
        let entries = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)

        if entries.Length > 8 then
            None
        else
            let decoded =
                entries
                |> Array.map (fun entry ->
                    match entry.Split('=', 2) with
                    | [| name; value |] -> Some(name, Uri.UnescapeDataString(value))
                    | _ -> None)

            if decoded |> Array.exists Option.isNone then
                None
            else
                let values = decoded |> Array.choose id |> Array.toList

                if values.Length <> (values |> List.map fst |> Set.ofList |> Set.count) then
                    None
                else
                    Some(Map.ofList values)

    let parseRequest expectedHost expectedIssuer expectedState (source: string) =
        let lines = source.Split("\r\n", StringSplitOptions.None)

        match lines |> Array.tryHead with
        | None -> Error "OIDC_CALLBACK_INVALID"
        | Some first ->
            match first.Split(' ', StringSplitOptions.None) with
            | [| "GET"; target; "HTTP/1.1" |] ->
                let hostHeaders =
                    lines
                    |> Array.choose (fun line ->
                        if line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase) then
                            Some(line.Substring(5).Trim())
                        else
                            None)

                let mutable uri = Unchecked.defaultof<Uri>

                if
                    not (target.StartsWith("/", StringComparison.Ordinal))
                    || target.StartsWith("//", StringComparison.Ordinal)
                    || not (
                        Uri.TryCreate("http://" + expectedHost + target, UriKind.Absolute, &uri)
                    )
                then
                    Error "OIDC_CALLBACK_INVALID"
                else
                    match pairs uri.Query with
                    | Some values when
                        hostHeaders = [| expectedHost |]
                        && uri.AbsolutePath = "/"
                        && values
                           |> Map.tryFind "state"
                           |> Option.exists (OidcClient.stateMatches expectedState)
                        && (values |> Map.tryFind "iss" |> Option.forall ((=) expectedIssuer))
                        && values |> Map.containsKey "error" |> not
                        ->
                        match values |> Map.tryFind "code" with
                        | Some code when code.Length > 0 && code.Length <= 4096 -> Ok code
                        | _ -> Error "OIDC_CALLBACK_INVALID"
                    | _ -> Error "OIDC_CALLBACK_INVALID"
            | _ -> Error "OIDC_CALLBACK_INVALID"

    let private readHeaders (stream: NetworkStream) cancelled =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let one = Array.zeroCreate<byte> 1
            let mutable count = 0
            let mutable doneReading = false

            while count < buffer.Length && not doneReading do
                let! received = stream.ReadAsync(one.AsMemory(), cancelled)

                if received = 0 then
                    doneReading <- true
                else
                    buffer[count] <- one[0]
                    count <- count + 1

                    if
                        count >= 4
                        && buffer[count - 4 .. count - 1] =
                            [| byte '\r'; byte '\n'; byte '\r'; byte '\n' |]
                    then
                        doneReading <- true

            if
                count >= 4
                && count < buffer.Length
                && buffer[count - 4 .. count - 1] = [| byte '\r'; byte '\n'; byte '\r'; byte '\n' |]
                && (buffer[.. count - 1]
                    |> Array.forall (fun value ->
                        value = 13uy || value = 10uy || (value >= 32uy && value <= 126uy)))
            then
                return Ok(Encoding.ASCII.GetString(buffer, 0, count))
            else
                return Error "OIDC_CALLBACK_INVALID"
        }

    let private respond (stream: NetworkStream) success cancelled =
        task {
            let status = if success then "200 OK" else "400 Bad Request"

            let body =
                if success then
                    "Authentication complete."
                else
                    "Authentication refused."

            let content = Encoding.ASCII.GetBytes(body)

            let header =
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=us-ascii\r\nCache-Control: no-store\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"
                )

            do! stream.WriteAsync(header.AsMemory(), cancelled)
            do! stream.WriteAsync(content.AsMemory(), cancelled)
            do! stream.FlushAsync(cancelled)
        }

    let receive
        (listener: TcpListener)
        (issuer: string)
        (state: string)
        (cancelled: CancellationToken)
        =
        task {
            use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelled)
            timeout.CancelAfter(TimeSpan.FromMinutes(2.))

            try
                use! client = listener.AcceptTcpClientAsync(timeout.Token)
                use stream = client.GetStream()
                let! request = readHeaders stream timeout.Token
                let host = (listener.LocalEndpoint :?> IPEndPoint).ToString()

                let result = request |> Result.bind (parseRequest host issuer state)

                do! respond stream (Result.isOk result) timeout.Token
                return result
            with
            | :? OperationCanceledException
            | :? IO.IOException
            | :? SocketException -> return Error "OIDC_CALLBACK_UNAVAILABLE"
        }
