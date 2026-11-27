namespace ClaimCore.Cli

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal ExportDownloadFailure =
    | Local of ClientFailure
    | Service of CliReply
    | Export of ExportFailure

module internal ExportBinaryCall =
    let private invalid =
        Error(ExportDownloadFailure.Local ClientFailure.InvalidResponse)

    let read
        frame
        selection
        (window: ExportPageWindow option)
        installation
        (response: HttpResponseMessage)
        token
        =
        task {
            let headers = response.Content.Headers
            let media = headers.ContentType |> Option.ofObj |> Option.map _.MediaType

            if media = Some "application/json" then
                if response.StatusCode <> HttpStatusCode.OK then
                    let! reply = RemoteServiceCall.readResponse frame response token
                    return Error(ExportDownloadFailure.Service reply)
                else
                    do!
                        response.Content.LoadIntoBufferAsync(
                            int64 Limits.MaximumResponseBytes,
                            token
                        )

                    let! bytes = response.Content.ReadAsByteArrayAsync token

                    return
                        match
                            Codec.decode Limits.MaximumResponseBytes ExportTransport.failure bytes
                        with
                        | Ok failure -> Error(ExportDownloadFailure.Export failure)
                        | Error _ -> invalid
            else
                let next = RemoteServiceCall.singleton ExportTransport.nextHeader response.Headers

                let validCursor =
                    if response.Headers.Contains ExportTransport.nextHeader then
                        window.IsSome
                        && (next
                            |> Option.exists (fun cursor ->
                                cursor.Length > 0
                                && cursor.Length <= ExportTransport.maximumContinuationCharacters))
                    else
                        true

                let uncached =
                    response.Headers.CacheControl
                    |> Option.ofObj
                    |> Option.exists (fun cache -> cache.NoStore && cache.NoTransform)

                let length = headers.ContentLength

                if
                    response.StatusCode <> HttpStatusCode.OK
                    || not uncached
                    || not validCursor
                    || not (RemoteServiceCall.confirms installation response)
                    || RemoteServiceCall.singleton "Content-Type" headers
                       <> Some ExportTransport.mediaType
                    || RemoteServiceCall.singleton "Content-Encoding" headers <> Some "identity"
                    || RemoteServiceCall.singleton "Content-Disposition" headers
                       <> Some(ExportTransport.disposition (ExportTransport.filename selection))
                    || not length.HasValue
                    || length.Value < 0L
                    || length.Value > int64 Limits.MaximumResponseBytes
                then
                    return invalid
                else
                    use! stream = response.Content.ReadAsStreamAsync token
                    let bytes = GC.AllocateUninitializedArray<byte>(int length.Value)
                    do! stream.ReadExactlyAsync(bytes.AsMemory(), token)
                    let! extra = stream.ReadAsync((Array.zeroCreate<byte> 1).AsMemory(), token)

                    return if extra = 0 then Ok(bytes, next) else invalid
        }

    let download
        (client: HttpClient)
        (access: RemoteServiceCall.Access)
        frame
        selection
        (window: ExportPageWindow option)
        (cancelled: CancellationToken)
        =
        task {
            match access.InstallationId with
            | None -> return Error(ExportDownloadFailure.Local ClientFailure.Configuration)
            | Some installation ->
                use deadline = HttpRequestDeadline.link client cancelled

                try
                    use request =
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            Uri(access.Service, ExportTransport.path)
                        )

                    request.Headers.Authorization <- access.Authorization
                    request.Headers.Add("ClaimCore-Installation", installation.ToString("D"))
                    request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)

                    request.Headers.Accept.Add(
                        MediaTypeWithQualityHeaderValue ExportTransport.successMediaType
                    )

                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

                    window
                    |> Option.iter (fun page ->
                        request.Headers.Add(
                            ExportTransport.pageSizeHeader,
                            page.Limit.ToString(CultureInfo.InvariantCulture)
                        )

                        page.After
                        |> Option.iter (fun cursor ->
                            request.Headers.Add(ExportTransport.afterHeader, cursor)))

                    let content =
                        new ByteArrayContent(
                            WireWriter.bytes (ExportTransport.request.Write selection)
                        )

                    content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
                    request.Content <- content

                    use! response =
                        client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token
                        )

                    return! read frame selection window installation response deadline.Token
                with
                | :? OperationCanceledException when cancelled.IsCancellationRequested ->
                    return Error(ExportDownloadFailure.Local ClientFailure.Cancelled)
                | :? IO.EndOfStreamException -> return invalid
                | :? OperationCanceledException
                | :? HttpRequestException
                | :? IO.IOException
                | :? InvalidOperationException ->
                    return Error(ExportDownloadFailure.Local ClientFailure.Unavailable)
        }
