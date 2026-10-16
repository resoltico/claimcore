namespace ClaimCore.Cli

open ClaimCore.ClientProtocol

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open ClaimCore.ClientContract

module RemoteServiceCall =
    [<Literal>]
    let MaximumResponseBytes = ProtocolProfile.MaximumResponseBytes

    let private invalid frame =
        CliReply.protocol "INVALID_RESPONSE"
        |> CliReply.forAttempt frame (DeliveryPolicy.missing true (Frames.endpoint frame).Mutates)

    let internal readResponse frame (response: HttpResponseMessage) cancelled =
        task {
            let media =
                response.Content.Headers.ContentType |> Option.ofObj |> Option.map _.MediaType

            if media <> Some "application/json" then
                return invalid frame
            else
                do! response.Content.LoadIntoBufferAsync(int64 MaximumResponseBytes, cancelled)
                let! bytes = response.Content.ReadAsByteArrayAsync cancelled

                if bytes.Length > MaximumResponseBytes then
                    return invalid frame
                elif response.StatusCode = HttpStatusCode.OK then
                    return
                        Frames.result frame bytes
                        |> Result.map CliReply.admitted
                        |> Result.defaultWith (fun _ -> invalid frame)
                else
                    match HostFailures.decode bytes with
                    | Ok reason when HostFailures.status reason = int response.StatusCode ->
                        let reply = Frames.serviceFailure frame reason |> CliReply.admitted

                        return reply
                    | _ -> return invalid frame
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

                return
                    CliReply.forAttempt frame (DeliveryPolicy.missing true endpoint.Mutates) reply
        }
