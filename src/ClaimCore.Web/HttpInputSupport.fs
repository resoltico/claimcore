namespace ClaimCore.Web

open ClaimCore.Contracts

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type LoginInput =
    {
        Credential: string
        AntiforgeryToken: string
    }

[<NoEquality; NoComparison>]
type PageInput = { Cursor: string option; Limit: int }

[<NoEquality; NoComparison>]
type RecoveryPageInput =
    {
        View: RecoveryListView
        Cursor: string option
        Limit: int
    }

[<NoEquality; NoComparison>]
type RecoveryInspectInput =
    {
        OperationId: Guid
        AttemptCursor: string option
        AttemptLimit: int
    }

[<NoEquality; NoComparison>]
type HistoryInput =
    {
        CaseReference: string
        Cursor: string option
        Limit: int
        Detail: HistoryDetail
    }

[<NoEquality; NoComparison>]
type ResolveInput =
    {
        OperationId: Guid
        RequestSha256: string
    }

[<NoEquality; NoComparison>]
type DismissInput =
    {
        OperationId: Guid
        RequestSha256: string
        Confirmed: bool
    }

module HttpInputSupport =
    exception InvalidInput of HttpInputProblem

    let fail message = raise (InvalidInput message)

    let properties (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail HttpInputProblem.ExpectedObject

        let values = element.EnumerateObject() |> Seq.toList
        let names = values |> List.map _.Name

        if names.Length <> (names |> Set.ofList |> Set.count) then
            fail HttpInputProblem.DuplicateProperty

        values |> List.map (fun property -> property.Name, property.Value) |> Map.ofList

    let exactProperties expected values =
        let names = values |> Map.toList |> List.map fst |> Set.ofList
        let allowed = expected |> Set.ofList

        if not (Set.isSubset names allowed) then
            fail HttpInputProblem.UnknownProperty

        values

    let required name values =
        values
        |> Map.tryFind name
        |> Option.defaultWith (fun () -> fail HttpInputProblem.MissingProperty)

    let stringValue (property: JsonElement) =
        if property.ValueKind <> JsonValueKind.String then
            fail HttpInputProblem.ExpectedString

        property.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> fail HttpInputProblem.NullForbidden)

    let optionalString name values =
        values |> Map.tryFind name |> Option.map stringValue

    let boolValue (property: JsonElement) =
        if
            property.ValueKind <> JsonValueKind.True
            && property.ValueKind <> JsonValueKind.False
        then
            fail HttpInputProblem.ExpectedBoolean

        property.GetBoolean()

    let integerValue (property: JsonElement) =
        let mutable value = 0

        if property.ValueKind <> JsonValueKind.Number || not (property.TryGetInt32(&value)) then
            fail HttpInputProblem.ExpectedInteger

        value

    let canonicalNonNegativeInt64 (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when
            parsed < Int64.MaxValue && parsed.ToString(CultureInfo.InvariantCulture) = value
            ->
            parsed
        | _ -> fail HttpInputProblem.InvalidRevision

    let operationIdValue (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> fail HttpInputProblem.InvalidUuid

    let digestValue (value: string) =
        let validCharacter character =
            ('0' <= character && character <= '9') || ('a' <= character && character <= 'f')

        if value.Length <> 64 || not (value |> Seq.forall validCharacter) then
            fail HttpInputProblem.InvalidDigest

        value

    let parse (bytes: byte array) read =
        try
            UTF8Encoding(false, true).GetCharCount(bytes) |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 32))

            if not (JsonUnicode.validDecodedStrings document.RootElement) then
                fail HttpInputProblem.InvalidUnicode

            Ok(read document.RootElement)
        with
        | InvalidInput message -> Error message
        | :? DecoderFallbackException -> Error HttpInputProblem.InvalidUtf8
        | :? JsonException -> Error HttpInputProblem.InvalidJson

    let readBounded limit (stream: Stream) =
        task {
            if limit < 1 then
                invalidArg (nameof limit) "The request byte limit must be positive."

            try
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 4096
                let mutable total = 0
                let mutable complete = false

                while total <= limit && not complete do
                    let! count = stream.ReadAsync(buffer, 0, buffer.Length)

                    if count = 0 then
                        complete <- true
                    elif count > limit - total then
                        total <- limit + 1
                    else
                        total <- total + count
                        output.Write(buffer, 0, count)

                if total > limit then
                    return Error HttpInputProblem.BodyTooLarge
                else
                    return Ok(output.ToArray())
            with
            | :? Microsoft.AspNetCore.Http.BadHttpRequestException as failure when
                failure.StatusCode = 413
                ->
                return Error HttpInputProblem.BodyTooLarge
            | :? OperationCanceledException -> return Error HttpInputProblem.BodyCancelled
            | :? IOException -> return Error HttpInputProblem.BodyUnreadable
        }

    let sourceDigest value =
        try
            value |> digestValue |> Ok
        with InvalidInput message ->
            Error message
