namespace ClaimCore.Cli

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open ClaimCore.Contracts

module RemoteServiceCall =
    let hostStatusMatches status (root: JsonElement) =
        let mutable reported = Unchecked.defaultof<JsonElement>
        let mutable value = 0

        root.ValueKind = JsonValueKind.Object
        && root.TryGetProperty("status", &reported)
        && reported.ValueKind = JsonValueKind.Number
        && reported.TryGetInt32(&value)
        && value = status

    let private mutates identifier =
        CliMutationCatalog.isMutation identifier

    let private afterDispatchFailure endpoint =
        if mutates endpoint then
            CliRemoteProblem.DeliveryUnconfirmed
        else
            CliRemoteProblem.ServiceUnavailable

    let private invalidReply identifier =
        if mutates identifier then
            CliRemoteProblem.DeliveryUnconfirmed
        else
            CliRemoteProblem.ServiceReplyInvalid

    let private content (request: RemoteRequest) =
        match request.Body with
        | RemoteBody.Json bytes ->
            let body = new ByteArrayContent(bytes)
            body.Headers.ContentType <- MediaTypeHeaderValue("application/json")
            body :> HttpContent
        | RemoteBody.Raw(bytes, mediaType, _) ->
            let body = new ByteArrayContent(bytes)
            body.Headers.ContentType <- MediaTypeHeaderValue(mediaType)
            body :> HttpContent

    let private artifactIdentity (operationId: Guid) (bytes: byte array) =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory bytes)
            let root = document.RootElement
            let mutable found = Unchecked.defaultof<JsonElement>

            root.ValueKind = JsonValueKind.Object
            && root.TryGetProperty("operationId", &found)
            && found.ValueKind = JsonValueKind.String
            && found.GetString() = operationId.ToString("D")
            && root.GetProperty("format").GetString() = "claimcore-recovery-artifact"
            && root.GetProperty("formatVersion").GetInt32() = 3
        with
        | :? JsonException
        | :? System.Collections.Generic.KeyNotFoundException
        | :? InvalidOperationException -> false

    let private readBounded maximum (response: HttpResponseMessage) cancelled =
        task {
            do! response.Content.LoadIntoBufferAsync(int64 maximum)
            let! bytes = response.Content.ReadAsByteArrayAsync(cancelled)
            return if bytes.Length <= maximum then Some bytes else None
        }

    let private export (request: RemoteRequest) (response: HttpResponseMessage) cancelled =
        task {
            match request.OperationId, request.Destination with
            | Some operationId, Some destination ->
                let expectedName = "claimcore-recovery-" + operationId.ToString("D") + ".json"
                let disposition = response.Content.Headers.ContentDisposition

                let filename =
                    match disposition |> Option.ofObj with
                    | None -> None
                    | Some value ->
                        let selected =
                            if String.IsNullOrWhiteSpace(value.FileNameStar) then
                                value.FileName
                            else
                                value.FileNameStar

                        selected |> Option.ofObj |> Option.map (fun name -> name.Trim('"'))

                let media =
                    response.Content.Headers.ContentType |> Option.ofObj |> Option.map _.MediaType

                if
                    filename <> Some expectedName
                    || media <> Some "application/vnd.claimcore.recovery+json"
                then
                    return Error CliRemoteProblem.DeliveryUnconfirmed
                else
                    match! readBounded 131072 response cancelled with
                    | Some bytes when artifactIdentity operationId bytes ->
                        match PrivateFiles.writeNew destination bytes with
                        | Ok() ->
                            return
                                Ok(
                                    CliRemoteWireCodec.exported
                                        request.Contract.Identifier
                                        operationId
                                        "application/vnd.claimcore.recovery+json"
                                )
                        | Error _ -> return Error CliRemoteProblem.PrivateDestination
                    | _ -> return Error CliRemoteProblem.DeliveryUnconfirmed
            | _ -> return Error CliRemoteProblem.DeliveryUnconfirmed
        }

    let private json request (response: HttpResponseMessage) cancelled =
        task {
            let media =
                response.Content.Headers.ContentType |> Option.ofObj |> Option.map _.MediaType

            if media <> Some "application/json" then
                return Error(invalidReply request.Contract.Identifier)
            else
                match! readBounded 131072 response cancelled with
                | None -> return Error(invalidReply request.Contract.Identifier)
                | Some bytes ->
                    try
                        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
                        let root = document.RootElement
                        let model = ContractProjection.current ()

                        if response.StatusCode = HttpStatusCode.OK then
                            let schema = WebSchemas.responseDocument model request.Contract

                            if SchemaValueValidation.verify schema root then
                                return
                                    Ok(CliRemoteWireCodec.result request.Contract.Identifier root)
                            else
                                return Error(invalidReply request.Contract.Identifier)
                        else
                            let schema =
                                {
                                    Identifier =
                                        "https://claimcore.local/contracts/web-v3.host-failure.schema.json"
                                    Title = "ClaimCore service host failure"
                                    Root = WebSchemaDefinitions.hostFailure
                                    Definitions = []
                                }

                            if
                                SchemaValueValidation.verify schema root
                                && hostStatusMatches (int response.StatusCode) root
                            then
                                return
                                    Ok(
                                        CliRemoteWireCodec.hostFailure
                                            request.Contract.Identifier
                                            root
                                    )
                            else
                                return Error(invalidReply request.Contract.Identifier)
                    with :? JsonException ->
                        return Error(invalidReply request.Contract.Identifier)
        }

    let private sendAuthorized
        (access: RemoteAccessSession)
        (body: RemoteRequest)
        (token: AccessToken)
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            let identifier = body.Contract.Identifier
            let location = Uri(access.Configuration.Service, body.Contract.Path.TrimStart('/'))
            use request = new HttpRequestMessage(HttpMethod.Post, location)
            request.Headers.Authorization <- token.Authorization
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            request.Content <- content body

            match body.Body with
            | RemoteBody.Raw(_, _, Some digest) ->
                request.Headers.Add("X-ClaimCore-Source-Sha256", digest)
            | _ -> ()

            try
                beforeSend ()

                use! response =
                    access.Client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancelled
                    )

                let! result =
                    if response.StatusCode = HttpStatusCode.OK && body.Destination.IsSome then
                        export body response cancelled
                    else
                        json body response cancelled

                return
                    match result with
                    | Ok value -> value
                    | Error reason -> CliRemoteWireCodec.localFailure identifier reason
            with
            | :? HttpRequestException
            | :? OperationCanceledException
            | :? IO.IOException
            | :? InvalidOperationException ->
                return CliRemoteWireCodec.localFailure identifier (afterDispatchFailure identifier)
        }

    let invoke
        (access: RemoteAccessSession)
        identifier
        (source: JsonElement)
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            match RemoteRequest.create identifier source with
            | Error reason ->
                let category =
                    if reason.StartsWith("PRIVATE_SOURCE", StringComparison.Ordinal) then
                        CliRemoteProblem.PrivateSource
                    else
                        CliRemoteProblem.ServiceReplyInvalid

                return CliRemoteWireCodec.localFailure identifier category
            | Ok body ->
                match! access.Token(cancelled) with
                | Error _ ->
                    return
                        CliRemoteWireCodec.localFailure identifier CliRemoteProblem.Authentication
                | Ok token -> return! sendAuthorized access body token beforeSend cancelled
        }
