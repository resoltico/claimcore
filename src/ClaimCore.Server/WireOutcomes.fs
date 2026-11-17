namespace ClaimCore.Server

open ClaimCore.Domain
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
                HttpHeaders.csv context
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

module internal WebWire =
    let csv (bytes: byte array) filename installation =
        if
            bytes.Length > Limits.MaximumResponseBytes
            || not (List.contains filename ExportTransport.filenames)
            || installation = Guid.Empty
        then
            invalidArg "bytes" "A complete bounded CSV response requires declared metadata."

        CompleteCsvResult(bytes, filename, installation) :> IResult

    let result status bytes =
        EncodedJsonResult(bytes, status) :> IResult

    let reply value =
        let bytes = WireWriter.bytes value

        if bytes.Length > Limits.MaximumResponseBytes then
            invalidOp "The encoded response exceeds the response limit."

        result 200 bytes

    let hostFailure reason =
        HostFailureCodecs.encode reason |> result (HostFailureReason.status reason)
