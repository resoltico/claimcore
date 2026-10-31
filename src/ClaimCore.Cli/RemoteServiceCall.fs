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
    [<NoEquality; NoComparison>]
    type Access =
        {
            Service: Uri
            InstallationId: Guid option
            Authorization: AuthenticationHeaderValue
        }

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

    let private sendBound
        (client: HttpClient)
        (access: Access)
        frame
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            let service = access.Service
            let authorization = access.Authorization
            let endpoint = Frames.endpoint frame
            use deadline = HttpRequestDeadline.link client cancelled

            use request =
                new HttpRequestMessage(Api.method endpoint, Uri(service, "/api/" + Api.id endpoint))

            access.InstallationId
            |> Option.iter (fun id ->
                request.Headers.Add("ClaimCore-Installation", id.ToString("D")))

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

    let send (client: HttpClient) (access: Access) frame beforeSend (cancelled: CancellationToken) =
        if Api.requiresInstallation (Frames.endpoint frame) && access.InstallationId.IsNone then
            System.Threading.Tasks.Task.FromResult(
                CliReply.local ClientFailure.Configuration
                |> CliReply.forAttempt frame DeliveryState.NotSent
            )
        else
            sendBound client access frame beforeSend cancelled

    /// Preparation uses the same bounded transport admission and preserves structured host failures.
    let context (client: HttpClient) (access: Access) (cancelled: CancellationToken) =
        task {
            let invocation =
                Frames.create Api.context ()
                |> Result.defaultWith (fun _ -> invalidOp "The empty context request must admit.")

            try
                let! reply = send client access invocation ignore cancelled

                match reply.Frame.Result with
                | FrameResult.Reply value ->
                    match ServiceReply.value Api.context value with
                    | Some(ClaimCore.Application.Contracts.Outcome.Succeeded context) ->
                        return Ok context
                    | _ -> return Error(CliReply.local ClientFailure.InvalidResponse)
                | _ -> return Error reply
            with
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? IO.IOException
            | :? InvalidOperationException -> return Error(CliReply.local ClientFailure.Unavailable)
        }
