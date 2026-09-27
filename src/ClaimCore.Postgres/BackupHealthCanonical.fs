namespace ClaimCore.Postgres

open System
open System.IO
open System.Text.Json

/// Recursive ASCII JSON canonicalization shared by owner issuance and runtime admission.
module internal BackupHealthCanonical =
    let rec private write (writer: Utf8JsonWriter) (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            writer.WriteStartObject()
            let properties = value.EnumerateObject() |> Seq.toArray

            Array.sortInPlaceWith
                (fun (left: JsonProperty) (right: JsonProperty) ->
                    StringComparer.Ordinal.Compare(left.Name, right.Name))
                properties

            for property in properties do
                writer.WritePropertyName(property.Name)
                write writer property.Value

            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()

            for item in value.EnumerateArray() do
                write writer item

            writer.WriteEndArray()
        | _ -> value.WriteTo(writer)

    let bytes (value: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer value
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let parse maximum (source: byte array) =
        if isNull (box source) || source.Length < 2 || source.Length > maximum then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(source))
                let root = document.RootElement

                if root.ValueKind <> JsonValueKind.Object || bytes root <> source then
                    None
                else
                    Some(root.Clone())
            with _ ->
                None

    let exact (value: JsonElement) (fields: string list) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
            names.Length = fields.Length && Set.ofArray names = Set.ofList fields
