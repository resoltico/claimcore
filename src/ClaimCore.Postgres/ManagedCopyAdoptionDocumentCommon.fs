namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application

/// Shared closed, sorted, newline-terminated JSON grammar for detached signed custody
/// receipts. Only commitments and ciphertext digests are retained; no private path is stored.
module internal ManagedCopyAdoptionDocumentCommon =
    let property (root: JsonElement) (name: string) = root.GetProperty(name)

    let text (root: JsonElement) name =
        (property root name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Signed copy adoption text is null.")

    let uuid root name =
        let raw = text root name
        let value = Guid.ParseExact(raw, "D")

        if value = Guid.Empty || value.ToString("D") <> raw then
            invalidOp "Signed copy adoption identity is noncanonical."

        value

    let number root name = (property root name).GetInt64()

    let digest root name =
        text root name |> ManagedCopyRegistrationAttestation.hex

    let instant root name =
        let raw = text root name

        let value =
            DateTimeOffset.ParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.None)

        if value.Offset <> TimeSpan.Zero || value.ToString("O") <> raw then
            invalidOp "Signed copy adoption time is noncanonical."

        value

    let origin (root: JsonElement) =
        let sequence = number root "preFenceSequence"
        let hash = digest root "preFenceHash"

        match text root "originKind", (property root "exportId").ValueKind with
        | "PRODUCT_EXPORT", JsonValueKind.String ->
            CopyAdoptionOrigin.ProductExport(uuid root "exportId", sequence, hash)
        | "ADOPTED_EXTERNAL", JsonValueKind.Null ->
            CopyAdoptionOrigin.AdoptedExternal(sequence, hash)
        | _ -> invalidOp "Signed copy adoption origin is invalid."

    let parse names format (canonical: byte array) (decode: JsonElement -> 'a) =
        if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let actual = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                if
                    root.ValueKind <> JsonValueKind.Object
                    || actual <> Set.ofList names
                    || (root.EnumerateObject() |> Seq.length) <> List.length names
                    || text root "format" <> format
                    || ManagedCopyRegistrationAttestation.canonicalBytes root <> canonical
                then
                    None
                else
                    Some(decode root)
            with _ ->
                None
