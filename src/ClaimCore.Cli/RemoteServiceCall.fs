namespace ClaimCore.Cli

open ClaimCore.Wire

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

module RemoteServiceCall =
    [<Literal>]
    let MaximumResponseBytes = Limits.MaximumResponseBytes

    let private invalid frame =
        CliReply.protocol ProtocolFailure.InvalidResponse
        |> CliReply.forAttempt
            frame
            (Delivery.knowledge
                (Frames.request frame |> WireRequest.endpoint)
                AttemptResult.DispatchedWithoutReply)

    let private encodeResult frame reply =
        try
            CliReply.result frame reply
        with :? ArgumentException ->
            // A valid HTTP body can exceed the frame cap once delivery metadata is added.
            invalid frame

    let private encodeServiceFailure frame failure =
        try
            CliReply.serviceFailure frame failure
        with :? ArgumentException ->
            invalid frame

    let internal readResponse frame (response: HttpResponseMessage) cancelled =
        task {
            let media =
                response.Content.Headers.ContentType
                |> Option.ofObj
                |> Option.exists (fun contentType ->
                    String.Equals(
                        contentType.MediaType,
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
                    return
                        Replies.decode (Frames.request frame |> WireRequest.endpoint) bytes
                        |> Result.bind (fun reply ->
                            if Correlation.matches (Frames.request frame) reply then
                                Ok reply
                            else
                                Error { Path = "$"; Reason = "CORRELATION" })
                        |> Result.map (encodeResult frame)
                        |> Result.defaultWith (fun _ -> invalid frame)
                else
                    match HostFailures.decode bytes with
                    | Ok reason when HostFailures.status reason.Reason = int response.StatusCode ->
                        let reply = encodeServiceFailure frame reason

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
            let endpoint = Frames.request frame |> WireRequest.endpoint
            use deadline = HttpRequestDeadline.link client cancelled

            use request =
                new HttpRequestMessage(
                    HttpMethod(Endpoint.method endpoint),
                    Uri(service, Endpoint.path endpoint)
                )

            request.Headers.Authorization <- authorization
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

            request.Headers.Add("ClaimCore-Contract", Fingerprint.value)

            if Endpoint.method endpoint = "POST" then
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
                    CliReply.forAttempt
                        frame
                        (Delivery.knowledge endpoint AttemptResult.DispatchedWithoutReply)
                        reply
        }
