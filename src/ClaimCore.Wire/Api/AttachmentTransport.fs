namespace ClaimCore.Wire

open System
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Presentation

[<RequireQualifiedAccess>]
type AttachmentTransfer =
    | Upload
    | Download
    | Preview

[<RequireQualifiedAccess>]
type AttachmentTransferFailure =
    | Rejected of HumanRejection
    | Failed of CoreFault

/// Complete attachment transfers are exchange media outside the ordinary JSON endpoint registry.
module AttachmentTransport =
    let all =
        [
            AttachmentTransfer.Upload
            AttachmentTransfer.Download
            AttachmentTransfer.Preview
        ]

    let token =
        function
        | AttachmentTransfer.Upload -> "attachment.upload"
        | AttachmentTransfer.Download -> "attachment.download"
        | AttachmentTransfer.Preview -> "attachment.preview"

    let path transfer = "/api/" + token transfer

    let tryPath value =
        all |> List.tryFind (fun transfer -> path transfer = value)

    let uploadPath = path AttachmentTransfer.Upload
    let downloadPath = path AttachmentTransfer.Download
    let previewPath = path AttachmentTransfer.Preview

    let multipartMediaType = "multipart/form-data"
    let manifestMediaType = "application/json"
    let manifestPartName = "manifest"
    let manifestFileName = "manifest.json"
    let filePartName = "file"
    let fileName = "payload.bin"
    let downloadMediaType = "application/octet-stream"
    let pngMediaType = "image/png"
    let jpegMediaType = "image/jpeg"
    let digestHeader = "Content-Digest"
    let transferDeadline = TimeSpan.FromSeconds 200.

    [<Literal>]
    let MaximumBoundaryLength = 70

    [<Literal>]
    let MaximumSectionHeaderBytes = 1024

    // Two section header blocks, bounded preamble and epilogue, and three delimiter lines.
    let maximumFramingBytes =
        4 * MaximumSectionHeaderBytes + 3 * (MaximumBoundaryLength + 110)

    let maximumUploadBytes manifestLimit =
        AttachmentLimits.FileBytes + manifestLimit + maximumFramingBytes

    let contentDigest digest =
        let base64 =
            digest
            |> AttachmentDigest.value
            |> Convert.FromHexString
            |> Convert.ToBase64String

        "sha-256=:" + base64 + ":"

    let disposition name =
        "attachment; filename=\""
        + AttachmentName.asciiFallback name
        + "\"; filename*=UTF-8''"
        + Uri.EscapeDataString(AttachmentName.value name)

    let request =
        (Codec.record (fun target id ->
            {
                AttachmentReadRequest.Target = target
                AttachmentId = id
            })
        : Fields<AttachmentReadRequest, _>)
        |> Codec.field "target" _.Target RequestCodecs.caseSelector
        |> Codec.field "attachmentId" _.AttachmentId AttachmentCodecs.id
        |> Codec.build
        |> Codec.named "AttachmentReadRequest"

    let private outcome =
        Codec.taggedOn
            "kind"
            "AttachmentTransferFailure"
            [
                Codec.variant
                    "REJECTED"
                    AttachmentTransferFailure.Rejected
                    (function
                    | AttachmentTransferFailure.Rejected value -> Some value
                    | _ -> None)
                    (Codec.record id |> Codec.field "rejection" id HumanDocumentCodecs.rejection)
                Codec.variant
                    "FAILED"
                    AttachmentTransferFailure.Failed
                    (function
                    | AttachmentTransferFailure.Failed value -> Some value
                    | _ -> None)
                    (Codec.record id |> Codec.field "fault" id DiagnosticCodecs.fault)
            ]

    let failure =
        Codec.record id
        |> Codec.field "outcome" id outcome
        |> Codec.build
        |> Codec.named "AttachmentTransferFailureReply"
