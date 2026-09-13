namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text.Json

[<RequireQualifiedAccess>]
module internal CorpusJson =
    let private parse (bytes: ReadOnlyMemory<byte>) =
        use document = JsonDocument.Parse(bytes)
        document.RootElement.Clone()

    let rewrite
        (value: JsonElement)
        (propertyName: string)
        (replacement: (Utf8JsonWriter -> unit) option)
        (addExtra: bool)
        =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()

        value.EnumerateObject()
        |> Seq.iter (fun property ->
            if property.Name <> propertyName then
                writer.WritePropertyName(property.Name)
                property.Value.WriteTo(writer)
            else
                match replacement with
                | Some write ->
                    writer.WritePropertyName(property.Name)
                    write writer
                | None -> ())

        if addExtra then
            writer.WriteBoolean("extra", true)

        writer.WriteEndObject()
        writer.Flush()
        parse buffer.WrittenMemory

    let textVariants identifier (value: JsonElement) (expected: string) replacements =
        let replace (replacement: string) =
            let source = value.GetRawText()

            let changed =
                source.Replace(
                    JsonSerializer.Serialize(expected),
                    JsonSerializer.Serialize(replacement),
                    StringComparison.Ordinal
                )

            if changed = source then
                invalidOp ("Corpus value does not contain projected text " + identifier + ".")

            use document = JsonDocument.Parse(changed)
            document.RootElement.Clone()

        replacements
        |> List.map (fun (suffix, replacement) -> identifier + "-" + suffix, replace replacement)

    let domainTextBoundaries (value: JsonElement) expected maximumCharacters =
        textVariants
            "text"
            value
            expected
            [
                "whitespace-only", "\u00a0"
                "leading-whitespace", "\u00a0" + expected
                "trailing-whitespace", expected + "\u3000"
                "c0-control", "prefix\u0000suffix"
                "c1-control", "prefix\u0080suffix"
                "four-byte-too-long", String.replicate (maximumCharacters + 1) "😀"
            ]

    let prefixedAndSuffixed identifier value expected =
        textVariants
            identifier
            value
            expected
            [ "prefixed", "x" + expected; "suffixed", expected + "x" ]

    let integerPropertyVariants identifier (value: JsonElement) property expected replacements =
        let source = value.GetRawText()
        let prefix = JsonSerializer.Serialize(property: string) + ":"
        let before = prefix + string expected

        replacements
        |> List.map (fun (suffix, replacement) ->
            let changed =
                source.Replace(before, prefix + string replacement, StringComparison.Ordinal)

            if changed = source then
                invalidOp ("Corpus value does not contain projected integer " + identifier + ".")

            use document = JsonDocument.Parse(changed)
            identifier + "-" + suffix, document.RootElement.Clone())
