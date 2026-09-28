namespace ClaimCore.Database

open System
open System.Buffers
open System.Security.Cryptography
open System.Text.Json

/// The owner backup tools sign sorted, compact, ASCII JSON with one trailing newline.
/// This parser rejects duplicate keys and alternate spellings before any signature check.
module internal DatabaseRestoreCanonical =
    let private ascii (value: string) =
        value |> Seq.forall (fun character -> character >= ' ' && character <= '~')

    let rec private write (writer: Utf8JsonWriter) (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let fields = value.EnumerateObject() |> Seq.toList
            let names = fields |> List.map _.Name

            if names.Length <> (names |> Set.ofList |> Set.count) then
                invalidOp "Canonical JSON has a repeated property."

            writer.WriteStartObject()

            for field in fields |> List.sortBy _.Name do
                if not (ascii field.Name) then
                    invalidOp "Canonical JSON property is not ASCII."

                writer.WritePropertyName(field.Name)
                write writer field.Value

            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()

            for item in value.EnumerateArray() do
                write writer item

            writer.WriteEndArray()
        | JsonValueKind.String ->
            let text = value.GetString() |> Option.ofObj |> Option.defaultValue ""

            if not (ascii text) then
                invalidOp "Canonical JSON value is not ASCII."

            writer.WriteStringValue(text)
        | JsonValueKind.Number ->
            let mutable number = 0L

            if not (value.TryGetInt64(&number)) then
                invalidOp "Canonical JSON number is not an integer."

            writer.WriteNumberValue(number)
        | JsonValueKind.True -> writer.WriteBooleanValue(true)
        | JsonValueKind.False -> writer.WriteBooleanValue(false)
        | JsonValueKind.Null -> writer.WriteNullValue()
        | _ -> invalidOp "Canonical JSON token is unsupported."

    let parse (bytes: byte array) =
        try
            let options =
                JsonDocumentOptions(
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                    MaxDepth = 32
                )

            let document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), options)
            let output = ArrayBufferWriter<byte>()
            use writer = new Utf8JsonWriter(output)
            write writer document.RootElement
            writer.Flush()
            let expected = Array.zeroCreate<byte>(output.WrittenCount + 1)
            output.WrittenSpan.CopyTo(expected.AsSpan())
            expected[expected.Length - 1] <- byte '\n'

            if CryptographicOperations.FixedTimeEquals(expected, bytes) then
                Some document
            else
                document.Dispose()
                None
        with _ ->
            None

    let exactProperties names (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.map _.Name |> Seq.toList |> List.sort) =
            (names |> List.sort)

    let text (name: string) (root: JsonElement) = root.GetProperty(name).GetString()
    let number (name: string) (root: JsonElement) = root.GetProperty(name).GetInt64()
    let flag (name: string) (root: JsonElement) = root.GetProperty(name).GetBoolean()
