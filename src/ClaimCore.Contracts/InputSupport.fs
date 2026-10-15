namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type HttpInputProblem =
    | ExpectedObject
    | DuplicateProperty
    | UnknownProperty
    | MissingProperty
    | ExpectedString
    | NullForbidden
    | ExpectedBoolean
    | ExpectedInteger
    | InvalidRevision
    | InvalidUuid
    | InvalidDigest
    | InvalidUnicode
    | InvalidUtf8
    | InvalidJson
    | BodyTooLarge
    | BodyUnreadable
    | BodyCancelled
    | UnknownCommand
    | MissingCommandValue
    | CorrectionAction
    | PageRange
    | HistoryDetail
    | InvalidPrincipal
    | InvalidRole
    | InvalidAction

module internal HttpInputSupport =
    exception InvalidInput of HttpInputProblem
    let fail reason = raise (InvalidInput reason)

    let properties (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail HttpInputProblem.ExpectedObject

        let values = element.EnumerateObject() |> Seq.toList

        if (values |> List.map _.Name |> Set.ofList |> Set.count) <> values.Length then
            fail HttpInputProblem.DuplicateProperty

        values |> List.map (fun property -> property.Name, property.Value) |> Map.ofList

    let exactProperties expected values =
        if values |> Map.exists (fun name _ -> not (List.contains name expected)) then
            fail HttpInputProblem.UnknownProperty

        values

    let required name values =
        Map.tryFind name values
        |> Option.defaultWith (fun () -> fail HttpInputProblem.MissingProperty)

    let stringValue (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            fail HttpInputProblem.ExpectedString

        element.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> fail HttpInputProblem.NullForbidden)

    let optionalString name values =
        Map.tryFind name values |> Option.map stringValue

    let boolValue (element: JsonElement) =
        if
            element.ValueKind <> JsonValueKind.True
            && element.ValueKind <> JsonValueKind.False
        then
            fail HttpInputProblem.ExpectedBoolean

        element.GetBoolean()

    let integerValue (element: JsonElement) =
        let mutable value = 0

        if element.ValueKind <> JsonValueKind.Number || not (element.TryGetInt32(&value)) then
            fail HttpInputProblem.ExpectedInteger

        value

    let operationIdValue (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> fail HttpInputProblem.InvalidUuid

    let parse (bytes: byte array) read =
        try
            if isNull (box bytes) then
                fail HttpInputProblem.BodyUnreadable

            if bytes.Length > 16 * 1024 * 1024 then
                fail HttpInputProblem.BodyTooLarge

            UTF8Encoding(false, true).GetCharCount bytes |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 32))

            if not (JsonUnicode.validDecodedStrings document.RootElement) then
                fail HttpInputProblem.InvalidUnicode

            Ok(read document.RootElement)
        with
        | InvalidInput reason -> Error reason
        | :? DecoderFallbackException -> Error HttpInputProblem.InvalidUtf8
        | :? JsonException -> Error HttpInputProblem.InvalidJson
        | :? InvalidOperationException
        | :? ArgumentException -> Error HttpInputProblem.InvalidJson
