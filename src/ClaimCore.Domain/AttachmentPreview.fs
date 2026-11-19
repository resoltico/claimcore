namespace ClaimCore.Domain

open System
open System.Buffers.Binary

[<RequireQualifiedAccess>]
type AttachmentPreviewCandidate =
    | Raster
    | Document

[<RequireQualifiedAccess>]
type AttachmentPreview =
    | Png
    | Jpeg
    | Pdf

module AttachmentPreview =
    [<Literal>]
    let MaximumSide = 8192u

    [<Literal>]
    let MaximumPixels = 16_000_000UL

    let candidate name =
        match AttachmentName.suffix name |> Option.map _.ToLowerInvariant() with
        | Some("png" | "jpg" | "jpeg") -> Some AttachmentPreviewCandidate.Raster
        | Some "pdf" -> Some AttachmentPreviewCandidate.Document
        | _ -> None

    let private bounded width height =
        width > 0u
        && height > 0u
        && width <= MaximumSide
        && height <= MaximumSide
        && uint64 width * uint64 height <= MaximumPixels

    let private pngSignature = [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |]

    // Ancillary chunks are opt-in. In particular, none of these can introduce a frame or
    // an independently compressed data stream.
    let private pngMetadata =
        Map.ofList
            [
                0x67414D41u, 4u // gAMA
                0x6348524Du, 32u // cHRM
                0x73524742u, 1u // sRGB
                0x70485973u, 9u // pHYs
            ]

    let private pngCrcTable =
        Array.init 256 (fun index ->
            (uint32 index, [ 1..8 ])
            ||> List.fold (fun crc _ ->
                if crc &&& 1u = 0u then
                    crc >>> 1
                else
                    (crc >>> 1) ^^^ 0xEDB88320u))

    let private pngCrc (bytes: ReadOnlySpan<byte>) =
        let mutable crc = 0xFFFFFFFFu

        for index = 0 to bytes.Length - 1 do
            let slot = int ((crc ^^^ uint32 bytes[index]) &&& 0xFFu)
            crc <- (crc >>> 8) ^^^ pngCrcTable[slot]

        ~~~crc

    let private png (bytes: ReadOnlySpan<byte>) =
        if bytes.Length < 45 || not (bytes.StartsWith(ReadOnlySpan<byte> pngSignature)) then
            false
        else
            let mutable offset = pngSignature.Length
            let mutable valid = true
            let mutable ended = false
            let mutable palette = 0u
            let mutable transparency = false
            let mutable color = 0uy
            let mutable depth = 0uy
            let mutable image = false
            let mutable nonempty = false
            let mutable seen = Set.empty

            while valid && not ended && bytes.Length - offset >= 12 do
                let length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4))
                let kind = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4))

                if length > uint32 (bytes.Length - offset - 12) then
                    valid <- false
                elif
                    pngCrc (bytes.Slice(offset + 4, 4 + int length))
                    <> BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + int length, 4))
                then
                    valid <- false
                else
                    let data = bytes.Slice(offset + 8, int length)

                    valid <-
                        match kind with
                        | 0x49484452u when offset = pngSignature.Length && length = 13u -> // IHDR
                            depth <- data[8]
                            color <- data[9]

                            let allowedDepth =
                                match color with
                                | 0uy -> List.contains depth [ 1uy; 2uy; 4uy; 8uy; 16uy ]
                                | 3uy -> List.contains depth [ 1uy; 2uy; 4uy; 8uy ]
                                | 2uy
                                | 4uy
                                | 6uy -> depth = 8uy || depth = 16uy
                                | _ -> false

                            allowedDepth
                            && data[10] = 0uy
                            && data[11] = 0uy
                            && data[12] <= 1uy
                            && bounded
                                (BinaryPrimitives.ReadUInt32BigEndian data)
                                (BinaryPrimitives.ReadUInt32BigEndian(data.Slice 4))
                        | _ when offset = pngSignature.Length -> false
                        | 0x504C5445u when not image && palette = 0u && not transparency -> // PLTE
                            palette <- length / 3u

                            length % 3u = 0u
                            && palette > 0u
                            && palette <= 256u
                            && color <> 0uy
                            && color <> 4uy
                            && (color <> 3uy || palette <= (1u <<< int depth))
                        | 0x74524E53u when not image && not transparency -> // tRNS
                            transparency <- true

                            match color with
                            | 0uy -> length = 2u
                            | 2uy -> length = 6u
                            | 3uy -> length > 0u && length <= palette
                            | _ -> false
                        | 0x49444154u -> // IDAT
                            image <- true
                            nonempty <- nonempty || length > 0u
                            color <> 3uy || palette > 0u
                        | 0x49454E44u -> // IEND
                            ended <- true
                            image && nonempty && length = 0u
                        | _ when not image && not (Set.contains kind seen) ->
                            seen <- Set.add kind seen
                            Map.tryFind kind pngMetadata = Some length
                        | _ -> false

                    offset <- offset + 12 + int length

            valid && ended && offset = bytes.Length

    let private jpeg (bytes: ReadOnlySpan<byte>) =
        if bytes.Length < 4 || bytes[0] <> 0xFFuy || bytes[1] <> 0xD8uy then
            false
        else
            let mutable offset = 2
            let mutable valid = true
            let mutable ended = false
            let mutable scans = 0
            let mutable entropy = false
            let mutable components = 0

            while valid && not ended && offset < bytes.Length do
                if entropy && bytes[offset] <> 0xFFuy then
                    offset <- offset + 1
                elif bytes[offset] <> 0xFFuy then
                    valid <- false
                else
                    while offset < bytes.Length && bytes[offset] = 0xFFuy do
                        offset <- offset + 1

                    if offset = bytes.Length then
                        valid <- false
                    else
                        let marker = bytes[offset]
                        offset <- offset + 1

                        match marker with
                        | 0x00uy when entropy -> ()
                        | restart when entropy && restart >= 0xD0uy && restart <= 0xD7uy -> ()
                        | 0xD9uy ->
                            ended <- scans > 0
                            valid <- ended
                        | _ when bytes.Length - offset >= 2 ->
                            entropy <- false

                            let length =
                                int (BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice offset))

                            if length < 2 || length > bytes.Length - offset then
                                valid <- false
                            else
                                let data = bytes.Slice(offset + 2, length - 2)

                                valid <-
                                    match marker with
                                    | 0xC0uy
                                    | 0xC2uy when components = 0 && data.Length >= 6 ->
                                        components <- int data[5]

                                        let height =
                                            BinaryPrimitives.ReadUInt16BigEndian(data.Slice 1)

                                        let width =
                                            BinaryPrimitives.ReadUInt16BigEndian(data.Slice 3)

                                        data[0] = 8uy
                                        && (components = 1 || components = 3 || components = 4)
                                        && data.Length = 6 + 3 * components
                                        && bounded (uint32 width) (uint32 height)
                                    | 0xDAuy when components > 0 && data.Length >= 6 ->
                                        let count = int data[0]
                                        entropy <- true
                                        scans <- scans + 1

                                        count > 0
                                        && count <= components
                                        && data.Length = 4 + 2 * count
                                    | 0xDDuy -> data.Length = 2 // DRI
                                    | 0xC4uy
                                    | 0xDBuy -> data.Length > 0 // DHT and DQT
                                    | 0xFEuy -> true // COM
                                    | app when app >= 0xE0uy && app <= 0xEFuy && scans = 0 -> true
                                    | _ -> false

                                offset <- offset + length
                        | _ -> valid <- false

            valid && ended && offset = bytes.Length

    let private pdfSignature = [| 37uy; 80uy; 68uy; 70uy; 45uy |]

    /// Rasters must fit the single-image profile. The browser still handles decoding and decode errors.
    let classify candidate (bytes: ReadOnlySpan<byte>) =
        if bytes.Length > AttachmentLimits.FileBytes then
            None
        else
            match candidate with
            | AttachmentPreviewCandidate.Raster when png bytes -> Some AttachmentPreview.Png
            | AttachmentPreviewCandidate.Raster when jpeg bytes -> Some AttachmentPreview.Jpeg
            | AttachmentPreviewCandidate.Document when
                bytes.StartsWith(ReadOnlySpan<byte> pdfSignature)
                ->
                Some AttachmentPreview.Pdf
            | _ -> None
