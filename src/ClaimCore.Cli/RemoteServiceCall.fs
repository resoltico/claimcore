namespace ClaimCore.Cli

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open ClaimCore.Contracts

module RemoteServiceCall =
    [<Literal>]
    let MaximumResponseBytes = 16 * 1024 * 1024

    let private invalid mutates =
        let reply = CliReply.protocol "INVALID_RESPONSE"
        if mutates then CliReply.unknown reply else reply

    let internal readResponse frame (response: HttpResponseMessage) cancelled =
        task {
            let endpoint = Frames.endpoint frame

            let media =
                response.Content.Headers.ContentType |> Option.ofObj |> Option.map _.MediaType

            if media <> Some "application/json" then
                return invalid endpoint.Mutates
            else
                do! response.Content.LoadIntoBufferAsync(int64 MaximumResponseBytes, cancelled)
                let! bytes = response.Content.ReadAsByteArrayAsync cancelled

                if bytes.Length > MaximumResponseBytes then
                    return invalid endpoint.Mutates
                elif response.StatusCode = HttpStatusCode.OK then
                    return
                        Frames.result endpoint.Identifier bytes
                        |> Result.map CliReply.admitted
                        |> Result.defaultWith (fun _ -> invalid endpoint.Mutates)
                else
                    match HostFailures.decode bytes with
                    | Ok reason when HostFailures.status reason = int response.StatusCode ->
                        let reply =
                            Frames.serviceFailure endpoint.Identifier reason |> CliReply.admitted

                        return
                            if
                                endpoint.Mutates && HostFailures.phase reason <> Some "NOT_STARTED"
                            then
                                CliReply.unknown reply
                            else
                                reply
                    | _ -> return invalid endpoint.Mutates
        }

    let send
        (client: HttpClient)
        (service: Uri)
        frame
        (authorization: AuthenticationHeaderValue)
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            let endpoint = Frames.endpoint frame
            use deadline = HttpRequestDeadline.link client cancelled

            use request =
                new HttpRequestMessage(HttpMethod(endpoint.Method), Uri(service, endpoint.Path))

            request.Headers.Authorization <- authorization
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

            request.Headers.Add(
                "ClaimCore-Contract",
                WireContractFingerprint.value EndpointCatalog.fingerprint
            )

            if endpoint.Method = "POST" then
                let content = new ByteArrayContent(Frames.inputBytes frame)
                content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
                request.Content <- content

            deadline.Token.ThrowIfCancellationRequested()
            beforeSend ()

            try
                use! response =
                    client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token
                    )

                return! readResponse frame response deadline.Token
            with
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? IO.IOException
            | :? InvalidOperationException ->
                let reply = CliReply.local ClientFailure.Unavailable
                return if endpoint.Mutates then CliReply.unknown reply else reply
        }
