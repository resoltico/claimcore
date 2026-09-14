namespace ClaimCore.Protocol

open System
open System.Text
open System.Text.Json

/// Local codec failure; not a server rejection or evidence of non-commitment.
type ProtocolError = { Code: string; Message: string }

exception internal InvalidWire of string

module internal JsonInput =
    let utf8 = UTF8Encoding(false, true)
    let reject code = raise (InvalidWire code)

    let unicode (text: string) =
        utf8.GetByteCount(text) |> ignore
        text

    let private propertyNames (element: JsonElement) =
        let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            unicode property.Name |> ignore

            if not (names.Add(property.Name)) then
                reject "DUPLICATE_PROPERTY"

    let rec private inspect (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            propertyNames element

            for property in element.EnumerateObject() do
                inspect property.Value
        | JsonValueKind.Array ->
            for item in element.EnumerateArray() do
                inspect item
        | JsonValueKind.String -> element.GetString() |> nonNull |> unicode |> ignore
        | _ -> ()

    let protect action =
        let error code =
            Error
                {
                    Code = code
                    Message = "The value does not satisfy the supported protocol."
                }

        try
            Ok(action ())
        with
        | InvalidWire code -> error code
        | :? DecoderFallbackException -> error "INVALID_UTF8"
        | :? EncoderFallbackException -> error "INVALID_UNICODE"
        | :? InvalidOperationException -> error "INVALID_UNICODE"
        | :? JsonException -> error "INVALID_JSON"
        | :? Text.RegularExpressions.RegexMatchTimeoutException -> error "LIMIT_EXCEEDED"

    let decode maximumBytes read (bytes: byte array) =
        if maximumBytes < 1 then
            invalidArg (nameof maximumBytes) "A positive byte budget is required."

        protect (fun () ->
            if bytes.Length > maximumBytes then
                reject "LIMIT_EXCEEDED"

            utf8.GetString(bytes) |> ignore

            let options =
                JsonDocumentOptions(MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow)

            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), options)
            inspect document.RootElement
            read document.RootElement)
