namespace ClaimCore.Server

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Extensions.Primitives
open Microsoft.Net.Http.Headers
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire

type private MultipartLengthExceeded() =
    inherit IOException("The multipart request exceeds its aggregate byte limit.")

/// MultipartReader bounds sections. This stream independently includes preamble, epilogue and delimiters.
type private CountedMultipartStream(input: Stream, maximum: int) =
    inherit Stream()

    let mutable remaining = maximum

    let count read =
        if read > remaining then
            raise (MultipartLengthExceeded())

        remaining <- remaining - read
        read

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = raise (NotSupportedException())

    override _.Read(buffer, offset, length) =
        input.Read(buffer, offset, min length (remaining + 1)) |> count

    override this.ReadAsync(buffer, offset, length, ct) =
        this.ReadAsync(Memory<byte>(buffer, offset, length), ct).AsTask()

    override _.ReadAsync(buffer: Memory<byte>, ct) =
        ValueTask<int>(
            task {
                let! read = input.ReadAsync(buffer.Slice(0, min buffer.Length (remaining + 1)), ct)
                return count read
            }
        )

    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

module internal AttachmentMultipart =
    let private equals left right =
        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private value (segment: StringSegment) =
        HeaderUtilities.RemoveQuotes(segment).Value |> Option.ofObj

    let private boundaryCharacter value =
        Char.IsAsciiLetterOrDigit value || "'()+_,-./:=?".Contains value

    let private parsed (ok, result) =
        if ok then Option.ofObj result else None

    let boundary context =
        match RequestGate.oneHeader "Content-Type" context with
        | Some contentType ->
            match MediaTypeHeaderValue.TryParse(StringSegment contentType) |> parsed with
            | Some media when
                equals media.MediaType.Value AttachmentTransport.multipartMediaType
                && media.Parameters.Count = 1
                ->
                media.Boundary
                |> value
                |> Option.filter (fun bound ->
                    bound.Length > 0
                    && bound.Length <= AttachmentTransport.MaximumBoundaryLength
                    && Seq.forall boundaryCharacter bound)
            | _ -> None
        | None -> None

    let acceptsIdentity (context: HttpContext) =
        match RequestGate.oneHeader "Content-Encoding" context with
        | None -> not (context.Request.Headers.ContainsKey "Content-Encoding")
        | Some encoding -> equals encoding "identity"

    let private headers name filename expectedType (section: MultipartSection) =
        match section.Headers |> Option.ofObj with
        | Some headers when headers.Count = 2 ->
            match headers.TryGetValue "Content-Disposition", headers.TryGetValue "Content-Type" with
            | (true, disposition), (true, media) when disposition.Count = 1 && media.Count = 1 ->
                match
                    ContentDispositionHeaderValue.TryParse(StringSegment(string disposition))
                    |> parsed,
                    MediaTypeHeaderValue.TryParse(StringSegment(string media)) |> parsed
                with
                | Some disposition, Some contentType ->
                    equals disposition.DispositionType.Value "form-data"
                    && disposition.Parameters.Count = 2
                    && value disposition.Name = Some name
                    && value disposition.FileName = Some filename
                    && equals contentType.MediaType.Value expectedType
                    && (contentType.Parameters.Count = 0
                        || (expectedType = AttachmentTransport.manifestMediaType
                            && contentType.Parameters.Count = 1
                            && (value contentType.Charset |> Option.exists (equals "utf-8"))))
                | _ -> false
            | _ -> false
        | _ -> false

    let private unreadable = Error BodyProblem.BodyUnreadable

    let read (header: string) manifestLimit admitIntent (body: Stream) (ct: CancellationToken) =
        task {
            try
                use counted =
                    new CountedMultipartStream(
                        body,
                        AttachmentTransport.maximumUploadBytes manifestLimit
                    )

                let multipart = MultipartReader(header, counted)
                multipart.HeadersCountLimit <- 2
                multipart.HeadersLengthLimit <- AttachmentTransport.MaximumSectionHeaderBytes

                let manifestHeaders =
                    headers
                        AttachmentTransport.manifestPartName
                        AttachmentTransport.manifestFileName
                        AttachmentTransport.manifestMediaType

                let fileHeaders =
                    headers
                        AttachmentTransport.filePartName
                        AttachmentTransport.fileName
                        AttachmentTransport.downloadMediaType

                match! multipart.ReadNextSectionAsync(ct) with
                | null -> return unreadable
                | manifest when not (manifestHeaders manifest) -> return unreadable
                | manifest ->
                    match! HttpBody.readWithin manifestLimit manifest.Body ct with
                    | Error problem -> return Error problem
                    | Ok encoded ->
                        match Codec.decode manifestLimit Api.operationExecute.Input encoded with
                        | Error _ -> return unreadable
                        | Ok submission ->
                            match admitIntent submission with
                            | Error _ -> return unreadable
                            | Ok intent ->
                                match! multipart.ReadNextSectionAsync(ct) with
                                | null -> return unreadable
                                | file when not (fileHeaders file) -> return unreadable
                                | file ->
                                    let length = int (AttachmentIntent.length intent)
                                    let bytes = Array.zeroCreate<byte> length

                                    let! filled =
                                        file.Body.ReadAtLeastAsync(
                                            Memory<byte> bytes,
                                            length,
                                            false,
                                            ct
                                        )

                                    if filled <> length then
                                        return unreadable
                                    else
                                        let! extra =
                                            file.Body.ReadAsync(
                                                Memory<byte>(Array.zeroCreate 1),
                                                ct
                                            )

                                        if extra <> 0 then
                                            let problem =
                                                if length = AttachmentLimits.FileBytes then
                                                    BodyProblem.BodyTooLarge
                                                else
                                                    BodyProblem.BodyUnreadable

                                            return Error problem
                                        else
                                            match! multipart.ReadNextSectionAsync(ct) with
                                            | null ->
                                                return Ok(submission, ReadOnlyMemory<byte> bytes)
                                            | _ -> return unreadable
            with
            | :? MultipartLengthExceeded -> return Error BodyProblem.BodyTooLarge
            | :? Microsoft.AspNetCore.Http.BadHttpRequestException as error when
                error.StatusCode = StatusCodes.Status413PayloadTooLarge
                ->
                return Error BodyProblem.BodyTooLarge
            | :? OperationCanceledException -> return Error BodyProblem.BodyCancelled
            | :? InvalidDataException
            | :? IOException -> return unreadable
        }
