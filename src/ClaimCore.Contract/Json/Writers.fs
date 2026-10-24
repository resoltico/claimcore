namespace ClaimCore.Contract

open System
open System.Globalization
open System.Text

module internal JsonWriting =
    let private appendString (output: StringBuilder) (value: string) =
        if not (JsonValue.isText value) then
            invalidArg (nameof value) "I-JSON strings require valid Unicode without noncharacters."

        output.Append('"') |> ignore

        for rune in value.EnumerateRunes() do
            let escaped =
                match rune.Value with
                | 0x22 -> "\\\""
                | 0x5C -> "\\\\"
                | 0x08 -> "\\b"
                | 0x09 -> "\\t"
                | 0x0A -> "\\n"
                | 0x0C -> "\\f"
                | 0x0D -> "\\r"
                | scalar when scalar < 0x20 ->
                    "\\u" + scalar.ToString("x4", CultureInfo.InvariantCulture)
                | _ -> rune.ToString()

            output.Append(escaped) |> ignore

        output.Append('"') |> ignore

    let private newline indented depth (output: StringBuilder) =
        if indented then
            output.Append('\n').Append(' ', depth * 2) |> ignore

    let private items indented depth (output: StringBuilder) write values =
        values
        |> List.iteri (fun index value ->
            if index > 0 then
                output.Append(',') |> ignore

            newline indented (depth + 1) output
            write value)

        if not (List.isEmpty values) then
            newline indented depth output

    let rec private write order indented depth (output: StringBuilder) value =
        match value with
        | JsonValue.String text -> appendString output text
        | JsonValue.Integer number ->
            if number < -JsonValue.MaximumInteger || number > JsonValue.MaximumInteger then
                invalidArg (nameof value) "Only safe integers are admitted."

            output.Append(number.ToString(CultureInfo.InvariantCulture)) |> ignore
        | JsonValue.Boolean flag -> output.Append(if flag then "true" else "false") |> ignore
        | JsonValue.Null -> output.Append("null") |> ignore
        | JsonValue.Array values ->
            output.Append('[') |> ignore
            items indented depth output (write order indented (depth + 1) output) values
            output.Append(']') |> ignore
        | JsonValue.Object members ->
            let names = members |> List.map fst

            if (Set.ofList names).Count <> names.Length then
                invalidArg (nameof value) "Duplicate object members are not I-JSON."

            output.Append('{') |> ignore
            items indented depth output (writeMember order indented depth output) (order members)
            output.Append('}') |> ignore

    and private writeMember order indented depth output (name, value) =
        appendString output name
        output.Append(if indented then ": " else ":") |> ignore
        write order indented (depth + 1) output value

    let bytes order indented value =
        let output = StringBuilder()
        write order indented 0 output value
        UTF8Encoding(false, true).GetBytes(output.ToString())

module CanonicalWriter =
    let bytes value =
        JsonWriting.bytes
            (List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b)))
            false
            value

module WireWriter =
    let bytes value = JsonWriting.bytes id false value
    let indentedBytes value = JsonWriting.bytes id true value
