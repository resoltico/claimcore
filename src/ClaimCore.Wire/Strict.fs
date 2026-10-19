namespace ClaimCore.Wire

open System
open System.Collections.Generic
open System.Text
open System.Text.Json

/// A wire problem names a JSON path and a fixed reason; it never echoes submitted content.
type Problem = { Path: string; Reason: string }

/// The strict byte profile: fatal UTF-8, bounded size and depth, no duplicate decoded member
/// names, Unicode scalars without U+0000 or noncharacters, integer-only number lexemes.
module Strict =
    [<Literal>]
    let MaximumDepth = 32

    [<Literal>]
    let MaximumSafeInteger = 9007199254740991L

    let problem path reason = Error { Path = path; Reason = reason }

    let internal isText (value: string) =
        let mutable valid = true
        let mutable index = 0

        while valid && index < value.Length do
            let c = value[index]

            if Char.IsHighSurrogate c then
                if index + 1 < value.Length && Char.IsLowSurrogate value[index + 1] then
                    let scalar = Char.ConvertToUtf32(c, value[index + 1])
                    valid <- (scalar &&& 0xFFFE) <> 0xFFFE
                    index <- index + 2
                else
                    valid <- false
            elif Char.IsLowSurrogate c then
                valid <- false
            else
                let scalar = int c

                valid <-
                    scalar <> 0
                    && not (scalar >= 0xFDD0 && scalar <= 0xFDEF)
                    && (scalar &&& 0xFFFE) <> 0xFFFE

                index <- index + 1

        valid

    let private integerLexeme (raw: string) =
        let digits = if raw.StartsWith '-' then raw.Substring 1 else raw

        digits.Length > 0
        && digits |> Seq.forall Char.IsAsciiDigit
        && (digits = "0" || digits[0] <> '0')

    let private readText getter =
        try
            getter () |> Option.ofObj
        with :? InvalidOperationException ->
            None

    let rec private walkObject path (element: JsonElement) =
        let names = HashSet<string>(StringComparer.Ordinal)

        element.EnumerateObject()
        |> Seq.fold
            (fun state property ->
                state
                |> Result.bind (fun () ->
                    match readText (fun () -> property.Name) with
                    | Some name when isText name && names.Add name ->
                        walk (path + "." + name) property.Value
                    | _ -> problem path "member names must be unique Unicode scalar text"))
            (Ok())

    and private walk path (element: JsonElement) : Result<unit, Problem> =
        match element.ValueKind with
        | JsonValueKind.Object -> walkObject path element
        | JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.indexed
            |> Seq.fold
                (fun state (index, item) ->
                    state |> Result.bind (fun () -> walk (sprintf "%s[%d]" path index) item))
                (Ok())
        | JsonValueKind.String ->
            match readText element.GetString with
            | Some text when isText text -> Ok()
            | _ -> problem path "string is not Unicode scalar text without U+0000 or noncharacters"
        | JsonValueKind.Number ->
            match element.TryGetInt64() with
            | true, value when
                integerLexeme (element.GetRawText())
                && value >= -MaximumSafeInteger
                && value <= MaximumSafeInteger
                ->
                Ok()
            | _ -> problem path "number is not a safe integer lexeme"
        | _ -> Ok()

    /// Parses bounded bytes and applies the profile. The caller disposes the document.
    let parse (maximumBytes: int) (bytes: byte array) : Result<JsonDocument, Problem> =
        if obj.ReferenceEquals(bytes, null) || bytes.Length > maximumBytes then
            problem "$" "document exceeds its byte limit"
        else
            try
                UTF8Encoding(false, true).GetCharCount bytes |> ignore

                let document =
                    JsonDocument.Parse(
                        ReadOnlyMemory bytes,
                        JsonDocumentOptions(
                            MaxDepth = MaximumDepth,
                            CommentHandling = JsonCommentHandling.Disallow,
                            AllowTrailingCommas = false
                        )
                    )

                match walk "$" document.RootElement with
                | Ok() -> Ok document
                | Error failure ->
                    document.Dispose()
                    Error failure
            with
            | :? DecoderFallbackException -> problem "$" "document is not UTF-8"
            | :? JsonException -> problem "$" "document is not one JSON value"
