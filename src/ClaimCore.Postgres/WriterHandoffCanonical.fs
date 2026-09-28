namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Text.Json

module internal WriterHandoffCanonical =
    let shape (bytes: byte array) (fields: Set<string>) stage =
        if isNull (box bytes) || bytes.Length < 2 || bytes.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement
                let properties = root.EnumerateObject() |> Seq.toArray

                if
                    root.ValueKind <> JsonValueKind.Object
                    || properties.Length <> fields.Count
                    || (properties |> Seq.map _.Name |> Set.ofSeq) <> fields
                    || ManagedCopyRegistrationAttestation.canonicalBytes root <> bytes
                    || ManagedCopyRegistrationAttestation.requiredText root "format"
                       <> "claimcore-writer-handoff-1"
                    || ManagedCopyRegistrationAttestation.requiredText root "stage" <> stage
                then
                    None
                else
                    Some(root.Clone())
            with _ ->
                None

    let text (root: JsonElement) name =
        ManagedCopyRegistrationAttestation.requiredText root name

    let number (root: JsonElement) name =
        ManagedCopyRegistrationAttestation.number root name

    let digest (root: JsonElement) name =
        text root name |> ManagedCopyRegistrationAttestation.hex

    let uuid (root: JsonElement) name =
        let raw = text root name
        let value = Guid.ParseExact(raw, "D")

        if value = Guid.Empty || raw <> value.ToString("D") then
            invalidOp "Writer handoff UUID is noncanonical."

        value

    let time (root: JsonElement) name =
        let raw = text root name

        let value =
            DateTimeOffset.ParseExact(
                raw,
                "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if value.Offset <> TimeSpan.Zero then
            invalidOp "Writer handoff time is noncanonical."

        value
