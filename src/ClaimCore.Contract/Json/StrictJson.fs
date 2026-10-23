namespace ClaimCore.Contract

open System
open System.Collections.Generic
open System.Text
open System.Text.Json

/// Fixed reasons and paths contain no submitted values or exception messages.
type Problem = { Path: string; Reason: string }

module StrictJson =
    [<Literal>]
    let MaximumDepth = 32

    let private reject reason = invalidArg "json" reason

    let private text (read: unit -> string) =
        let value = read ()

        if not (JsonValue.isText value) then
            reject "text is not Unicode scalar interchange text"

        value

    let private integer (element: JsonElement) =
        let raw = element.GetRawText()
        let digits = if raw.StartsWith '-' then raw.Substring 1 else raw
        let lexical = digits.Length > 0 && digits |> Seq.forall Char.IsAsciiDigit

        match element.TryGetInt64() with
        | true, value when
            lexical
            && value >= -JsonValue.MaximumInteger
            && value <= JsonValue.MaximumInteger
            ->
            JsonValue.Integer value
        | _ -> reject "number is not a safe integer lexeme"

    let rec ofElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)

            element.EnumerateObject()
            |> Seq.map (fun property ->
                let name = text (fun () -> property.Name)

                if not (names.Add name) then
                    reject "duplicate decoded member name"

                name, ofElement property.Value)
            |> Seq.toList
            |> JsonValue.Object
        | JsonValueKind.Array ->
            element.EnumerateArray() |> Seq.map ofElement |> Seq.toList |> JsonValue.Array
        | JsonValueKind.String ->
            text (fun () -> element.GetString() |> nonNull) |> JsonValue.String
        | JsonValueKind.Number -> integer element
        | JsonValueKind.True -> JsonValue.Boolean true
        | JsonValueKind.False -> JsonValue.Boolean false
        | JsonValueKind.Null -> JsonValue.Null
        | _ -> reject "unsupported JSON value"

    let read maximumBytes (bytes: byte array) =
        let fail reason = Error { Path = "$"; Reason = reason }

        if obj.ReferenceEquals(bytes, null) || bytes.Length > maximumBytes then
            fail "document exceeds its byte limit"
        else
            try
                UTF8Encoding(false, true).GetCharCount bytes |> ignore

                use document =
                    JsonDocument.Parse(
                        ReadOnlyMemory bytes,
                        JsonDocumentOptions(MaxDepth = MaximumDepth)
                    )

                Ok(ofElement document.RootElement)
            with
            | :? DecoderFallbackException -> fail "document is not UTF-8"
            | :? JsonException -> fail "document is not one JSON value"
            | :? ArgumentException -> fail "document violates the integer-only I-JSON profile"
            | :? InvalidOperationException -> fail "document contains malformed Unicode"
