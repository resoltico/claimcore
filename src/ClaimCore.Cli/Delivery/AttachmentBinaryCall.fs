namespace ClaimCore.Cli

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Wire

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal AttachmentDownloadFailure =
    | Local of ClientFailure
    | Host of HostFailure * contractIdentity: string option
    | Refused of AttachmentTransferFailure
    | Access of CliReply

module internal AttachmentBinaryCall =
    let private invalid =
        Error(AttachmentDownloadFailure.Local ClientFailure.InvalidResponse)

    let private length (header: string) =
        match Int32.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, length when
            length <= AttachmentLimits.FileBytes
            && header = length.ToString(CultureInfo.InvariantCulture)
            ->
            Some length
        | _ -> None

    let internal receive
        installation
        name
        (response: HttpResponseMessage)
        (token: CancellationToken)
        =
        task {
            let headers = response.Content.Headers
            let media = headers.ContentType |> Option.ofObj |> Option.map _.MediaType

            let uncached =
                response.Headers.CacheControl
                |> Option.ofObj
                |> Option.exists (fun cache -> cache.NoStore && cache.NoTransform)

            if media = Some "application/json" then
                do! response.Content.LoadIntoBufferAsync(int64 Limits.MaximumResponseBytes, token)
                let! bytes = response.Content.ReadAsByteArrayAsync token

                return
                    if
                        response.StatusCode = HttpStatusCode.OK
                        && RemoteServiceCall.confirms installation response
                    then
                        StrictJson.read Limits.MaximumResponseBytes bytes
                        |> Result.bind (AttachmentTransport.failure.Read "$")
                        |> function
                            | Ok failure -> Error(AttachmentDownloadFailure.Refused failure)
                            | Error _ -> invalid
                    else
                        match HostFailureCodecs.decode bytes with
                        | Ok failure when
                            HostFailureCodecs.matchesStatus (int response.StatusCode) failure
                            ->
                            Error(
                                AttachmentDownloadFailure.Host(
                                    failure,
                                    RemoteServiceCall.hostContractIdentity failure response
                                )
                            )
                        | _ -> invalid
            elif
                response.StatusCode <> HttpStatusCode.OK
                || RemoteServiceCall.singleton "Content-Type" headers
                   <> Some AttachmentTransport.downloadMediaType
                || not uncached
                || not (RemoteServiceCall.confirms installation response)
                || RemoteServiceCall.singleton "Content-Encoding" headers <> Some "identity"
                || not (
                    RemoteServiceCall.singleton "Content-Disposition" headers
                    |> Option.exists ((=) (AttachmentTransport.disposition name))
                )
            then
                return invalid
            else
                match
                    RemoteServiceCall.singleton "Content-Length" headers |> Option.bind length,
                    RemoteServiceCall.singleton AttachmentTransport.digestHeader response.Headers
                with
                | Some length, Some digest when
                    digest.Length = 54
                    && digest.StartsWith("sha-256=:", StringComparison.Ordinal)
                    && digest.EndsWith ':'
                    ->
                    let bytes = GC.AllocateUninitializedArray<byte> length
                    use! body = response.Content.ReadAsStreamAsync token
                    do! body.ReadExactlyAsync(bytes.AsMemory(), token)
                    let extra = Array.zeroCreate<byte> 1
                    let! remaining = body.ReadAsync(extra.AsMemory(), token)

                    if
                        remaining <> 0
                        || digest
                           <> (AttachmentDigest.hash (ReadOnlySpan<byte> bytes)
                               |> AttachmentTransport.contentDigest)
                    then
                        return invalid
                    else
                        return Ok bytes
                | _ -> return invalid
        }

    let download
        (client: HttpClient)
        (access: RemoteServiceCall.Access)
        (selection: AttachmentReadRequest)
        (name: AttachmentName)
        (cancelled: CancellationToken)
        =
        task {
            match access.InstallationId with
            | None -> return Error(AttachmentDownloadFailure.Local ClientFailure.Configuration)
            | Some installation ->
                use deadline = HttpRequestDeadline.link client cancelled

                try
                    use request =
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            Uri(access.Service, AttachmentTransport.downloadPath)
                        )

                    request.Headers.Authorization <- access.Authorization
                    request.Headers.Add("ClaimCore-Installation", installation.ToString("D"))
                    request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)

                    request.Headers.Accept.Add(
                        MediaTypeWithQualityHeaderValue AttachmentTransport.downloadMediaType
                    )

                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

                    let content =
                        new ByteArrayContent(
                            WireWriter.bytes (AttachmentTransport.request.Write selection)
                        )

                    content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
                    request.Content <- content

                    use! response =
                        client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token
                        )

                    return! receive installation name response deadline.Token
                with
                | :? OperationCanceledException when cancelled.IsCancellationRequested ->
                    return Error(AttachmentDownloadFailure.Local ClientFailure.Cancelled)
                | :? IO.EndOfStreamException -> return invalid
                | :? OperationCanceledException
                | :? HttpRequestException
                | :? IO.IOException
                | :? InvalidOperationException ->
                    return Error(AttachmentDownloadFailure.Local ClientFailure.Unavailable)
        }
