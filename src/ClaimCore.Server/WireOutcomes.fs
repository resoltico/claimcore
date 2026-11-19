namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

open System
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Server.Composition

[<Sealed>]
type private EncodedJsonResult(bytes: byte array, status: int) =
    interface IResult with
        member _.ExecuteAsync context =
            task {
                context.Response.StatusCode <- status
                context.Response.ContentType <- "application/json; charset=utf-8"
                context.Response.ContentLength <- int64 bytes.Length
                do! context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            }
            :> Task

[<Sealed>]
type private CompleteCsvResult(bytes: byte array, filename: string, installation: Guid) =
    interface IResult with
        member _.ExecuteAsync context =
            task {
                let stopping =
                    context.RequestServices
                        .GetRequiredService<IHostApplicationLifetime>()
                        .ApplicationStopping

                use cancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        context.RequestAborted,
                        stopping
                    )

                cancellation.Token.ThrowIfCancellationRequested()
                HttpHeaders.noTransform context
                context.Response.StatusCode <- StatusCodes.Status200OK
                context.Response.ContentType <- ExportTransport.mediaType
                context.Response.ContentLength <- int64 bytes.Length
                context.Response.Headers.ContentEncoding <- "identity"

                context.Response.Headers.ContentDisposition <-
                    Microsoft.Extensions.Primitives.StringValues(
                        "attachment; filename=\"" + filename + "\""
                    )

                context.Response.Headers["ClaimCore-Installation"] <-
                    Microsoft.Extensions.Primitives.StringValues(installation.ToString("D"))

                context.Response.Headers.CacheControl <- "no-store, no-transform, max-age=0"
                do! context.Response.Body.WriteAsync(bytes, cancellation.Token)
            }
            :> Task

[<Sealed>]
type private CompleteAttachmentResult
    (bytes: VerifiedAttachment, mediaType: string, disposition: string option, installation: Guid) =
    interface IResult with
        member _.ExecuteAsync context =
            task {
                let intent = VerifiedAttachment.intent bytes
                let memory = VerifiedAttachment.memory bytes
                HttpHeaders.noTransform context
                context.Response.StatusCode <- StatusCodes.Status200OK
                context.Response.ContentType <- mediaType
                context.Response.ContentLength <- int64 memory.Length
                context.Response.Headers.ContentEncoding <- "identity"

                context.Response.Headers[AttachmentTransport.digestHeader] <-
                    Microsoft.Extensions.Primitives.StringValues(
                        AttachmentIntent.digest intent |> AttachmentTransport.contentDigest
                    )

                context.Response.Headers["ClaimCore-Installation"] <- installation.ToString("D")

                disposition
                |> Option.iter (fun value -> context.Response.Headers.ContentDisposition <- value)

                do! context.Response.Body.WriteAsync(memory, context.RequestAborted)
            }
            :> Task

module internal WebWire =
    let csv (bytes: byte array) filename installation =
        if
            bytes.Length > Limits.MaximumResponseBytes
            || not (List.contains filename ExportTransport.filenames)
            || installation = Guid.Empty
        then
            invalidArg "bytes" "A complete bounded CSV response requires declared metadata."

        CompleteCsvResult(bytes, filename, installation) :> IResult

    let attachment transfer (content: AttachmentContent) installation =
        let name = content.Bytes |> VerifiedAttachment.intent |> AttachmentIntent.name

        let mediaType, disposition =
            match transfer, content.Preview with
            | AttachmentTransfer.Download, _ ->
                AttachmentTransport.downloadMediaType, Some(AttachmentTransport.disposition name)
            | AttachmentTransfer.Preview, Some AttachmentPreview.Png ->
                AttachmentTransport.pngMediaType, None
            | AttachmentTransfer.Preview, Some AttachmentPreview.Jpeg ->
                AttachmentTransport.jpegMediaType, None
            | _ -> invalidOp "An attachment response requires a confirmed transfer media type."

        CompleteAttachmentResult(content.Bytes, mediaType, disposition, installation) :> IResult

    let result status bytes =
        EncodedJsonResult(bytes, status) :> IResult

    let reply value =
        let bytes = WireWriter.bytes value

        if bytes.Length > Limits.MaximumResponseBytes then
            invalidOp "The encoded response exceeds the response limit."

        result 200 bytes

    let hostFailure reason =
        HostFailureCodecs.encode reason |> result (HostFailureReason.status reason)
