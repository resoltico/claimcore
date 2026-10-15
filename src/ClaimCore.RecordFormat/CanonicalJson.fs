namespace ClaimCore.RecordFormat

open System
open System.Globalization
open System.Text
open System.Text.Json

/// The integer-only I-JSON subset used by ClaimCore's canonical records.
[<RequireQualifiedAccess>]
type CanonicalValue =
    | Object of (string * CanonicalValue) list
    | Array of CanonicalValue list
    | String of string
    | Integer of int64
    | Boolean of bool
    | Null

module CanonicalJson =
    [<Literal>]
    let MaximumInteger = 9007199254740991L

    let private noncharacter scalar =
        (scalar >= 0xFDD0 && scalar <= 0xFDEF) || (scalar &&& 0xFFFE) = 0xFFFE

    let private appendString (output: StringBuilder) (value: string) =
        if obj.ReferenceEquals(value, null) then
            invalidArg (nameof value) "A JSON string cannot be null."

        UTF8Encoding(false, true).GetByteCount(value) |> ignore
        output.Append('"') |> ignore

        for rune in value.EnumerateRunes() do
            if noncharacter rune.Value then
                invalidArg (nameof value) "I-JSON excludes Unicode noncharacters."

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

    let private sortedMembers members =
        let ordered =
            members |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        let names = ordered |> List.map fst

        if (Set.ofList names).Count <> names.Length then
            invalidArg (nameof members) "Duplicate object members are not I-JSON."

        ordered

    let rec private write (output: StringBuilder) value =
        match value with
        | CanonicalValue.String value -> appendString output value
        | CanonicalValue.Integer value ->
            if value < -MaximumInteger || value > MaximumInteger then
                invalidArg (nameof value) "Only safe integers are admitted."

            output.Append(value.ToString(CultureInfo.InvariantCulture)) |> ignore
        | CanonicalValue.Boolean value -> output.Append(if value then "true" else "false") |> ignore
        | CanonicalValue.Null -> output.Append("null") |> ignore
        | CanonicalValue.Array values ->
            output.Append('[') |> ignore

            values
            |> List.iteri (fun index item ->
                if index > 0 then
                    output.Append(',') |> ignore

                write output item)

            output.Append(']') |> ignore
        | CanonicalValue.Object members ->
            output.Append('{') |> ignore

            sortedMembers members
            |> List.iteri (fun index (name, item) ->
                if index > 0 then
                    output.Append(',') |> ignore

                appendString output name
                output.Append(':') |> ignore
                write output item)

            output.Append('}') |> ignore

    let encode value =
        let output = StringBuilder()
        write output value
        UTF8Encoding(false, true).GetBytes(output.ToString())

    /// Strict lexical integers, decoded member names and duplicate detection survive JSON parsing.
    let rec ofElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.map (fun memberValue -> memberValue.Name, ofElement memberValue.Value)
            |> Seq.toList
            |> sortedMembers
            |> CanonicalValue.Object
        | JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.map ofElement
            |> Seq.toList
            |> CanonicalValue.Array
        | JsonValueKind.String ->
            element.GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidArg (nameof element) "Expected a JSON string.")
            |> CanonicalValue.String
        | JsonValueKind.True -> CanonicalValue.Boolean true
        | JsonValueKind.False -> CanonicalValue.Boolean false
        | JsonValueKind.Null -> CanonicalValue.Null
        | JsonValueKind.Number ->
            match element.TryGetInt64() with
            | true, value when value >= -MaximumInteger && value <= MaximumInteger ->
                CanonicalValue.Integer value
            | _ -> invalidArg (nameof element) "Only safe integer JSON lexemes are admitted."
        | _ -> invalidArg (nameof element) "Unsupported JSON value."
