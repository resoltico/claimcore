namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open ClaimCore.Json
open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading

module RemoteServiceCall =
    [<Literal>]
    let MaximumResponseBytes = Limits.MaximumResponseBytes

    let private invalid frame =
        CliReply.protocol CliFailure.InvalidResponse
        |> CliReply.forAttempt frame DeliveryState.Uncertain

    let private encode frame make =
        try
            make ()
        with :? ArgumentException ->
            invalid frame

    let private readReply frame bytes =
        encode frame (fun () ->
            StrictJson.read MaximumResponseBytes bytes
            |> Result.mapError (fun _ -> CliFailure.InvalidResponse)
            |> Result.bind (CliReply.result frame)
            |> Result.defaultWith (fun _ -> invalid frame))

    let internal readResponse frame (response: HttpResponseMessage) cancelled =
        task {
            let media =
                response.Content.Headers.ContentType
                |> Option.ofObj
                |> Option.exists (fun value ->
                    String.Equals(
                        value.MediaType,
                        "application/json",
                        StringComparison.OrdinalIgnoreCase
                    ))

            if not media then
                return invalid frame
            else
                do! response.Content.LoadIntoBufferAsync(int64 MaximumResponseBytes, cancelled)
                let! bytes = response.Content.ReadAsByteArrayAsync cancelled

                if bytes.Length > MaximumResponseBytes then
                    return invalid frame
                elif response.StatusCode = HttpStatusCode.OK then
                    return readReply frame bytes
                else
                    match HostFailureCodecs.decode bytes with
                    | Ok failure when
                        HostFailureCodecs.matchesStatus (int response.StatusCode) failure
                        ->
                        return encode frame (fun () -> CliReply.serviceFailure frame failure)
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
                new HttpRequestMessage(Api.method endpoint, Uri(service, "/api/" + Api.id endpoint))

            request.Headers.Authorization <- authorization
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)

            if Api.method endpoint = HttpMethod.Post then
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
                return
                    CliReply.local ClientFailure.Unavailable
                    |> CliReply.forAttempt frame DeliveryState.Uncertain
        }
