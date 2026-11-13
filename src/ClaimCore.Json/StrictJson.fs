namespace ClaimCore.Json

open System
open System.Collections.Generic
open System.Text
open System.Text.Json

/// Fixed reasons and paths contain no submitted values or exception messages.
type Problem = { Path: string; Reason: string }

[<RequireQualifiedAccess>]
type JsonFailureReason =
    | NotJson
    | DuplicateKey
    | TooLarge
    | Schema

type JsonReadFailure =
    {
        Problem: Problem
        Kind: JsonFailureReason
    }

module StrictJson =
    [<Literal>]
    let MaximumDepth = 32

    exception private ProfileViolation of string
    exception private DuplicateMember

    let private reject reason = raise (ProfileViolation reason)

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

    let rec private ofElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let names = HashSet<string>(StringComparer.Ordinal)

            element.EnumerateObject()
            |> Seq.map (fun property ->
                let name = text (fun () -> property.Name)

                if not (names.Add name) then
                    raise DuplicateMember

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

    let readClassified maximumBytes (bytes: byte array) =
        let fail kind reason =
            Error
                {
                    Problem = { Path = "$"; Reason = reason }
                    Kind = kind
                }

        if obj.ReferenceEquals(bytes, null) || bytes.Length > maximumBytes then
            fail JsonFailureReason.TooLarge "document exceeds its byte limit"
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
            | DuplicateMember ->
                fail
                    JsonFailureReason.DuplicateKey
                    "document violates the integer-only I-JSON profile"
            | ProfileViolation _ ->
                fail JsonFailureReason.Schema "document violates the integer-only I-JSON profile"
            | :? DecoderFallbackException -> fail JsonFailureReason.NotJson "document is not UTF-8"
            | :? JsonException -> fail JsonFailureReason.NotJson "document is not one JSON value"
            | :? ArgumentException ->
                fail JsonFailureReason.Schema "document violates the integer-only I-JSON profile"
            | :? InvalidOperationException ->
                fail JsonFailureReason.Schema "document contains malformed Unicode"

    let read maximumBytes bytes =
        readClassified maximumBytes bytes |> Result.mapError _.Problem
