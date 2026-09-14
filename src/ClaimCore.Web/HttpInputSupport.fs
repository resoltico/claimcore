namespace ClaimCore.Web

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
    exception InvalidInput of string

    let fail message = raise (InvalidInput message)

    let properties (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail "Expected a JSON object."

        let values = element.EnumerateObject() |> Seq.toList
        let names = values |> List.map _.Name

        if names.Length <> (names |> Set.ofList |> Set.count) then
            fail "Duplicate JSON properties are not accepted."

        values |> List.map (fun property -> property.Name, property.Value) |> Map.ofList

    let exactProperties expected values =
        let names = values |> Map.toList |> List.map fst |> Set.ofList
        let allowed = expected |> Set.ofList

        if not (Set.isSubset names allowed) then
            fail "The JSON object contains an unknown property."

        values

    let required name values =
        values
        |> Map.tryFind name
        |> Option.defaultWith (fun () -> fail "A required JSON property is missing.")

    let stringValue (property: JsonElement) =
        if property.ValueKind <> JsonValueKind.String then
            fail "Expected a JSON string."

        property.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> fail "JSON null is not accepted here.")

    let optionalString name values =
        values |> Map.tryFind name |> Option.map stringValue

    let boolValue (property: JsonElement) =
        if
            property.ValueKind <> JsonValueKind.True
            && property.ValueKind <> JsonValueKind.False
        then
            fail "Expected a JSON boolean."

        property.GetBoolean()

    let integerValue (property: JsonElement) =
        let mutable value = 0

        if property.ValueKind <> JsonValueKind.Number || not (property.TryGetInt32(&value)) then
            fail "Expected a JSON integer."

        value

    let canonicalNonNegativeInt64 (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when
            parsed < Int64.MaxValue && parsed.ToString(CultureInfo.InvariantCulture) = value
            ->
            parsed
        | _ -> fail "Expected a canonical non-negative revision below Int64.MaxValue."

    let operationIdValue (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> fail "Use one non-empty canonical lowercase UUID."

    let digestValue (value: string) =
        let validCharacter character =
            ('0' <= character && character <= '9') || ('a' <= character && character <= 'f')

        if value.Length <> 64 || not (value |> Seq.forall validCharacter) then
            fail "Use one lowercase SHA-256 digest."

        value

    let parse (bytes: byte array) read =
        try
            UTF8Encoding(false, true).GetCharCount(bytes) |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 32))

            if not (JsonUnicode.validDecodedStrings document.RootElement) then
                fail "Malformed Unicode escape in JSON text."

            Ok(read document.RootElement)
        with
        | InvalidInput message -> Error message
        | :? DecoderFallbackException -> Error "The request must be valid UTF-8."
        | :? JsonException -> Error "Malformed JSON or an unsupported JSON shape."

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
                    return Error "The request exceeds the configured byte limit."
                else
                    return Ok(output.ToArray())
            with :? IOException ->
                return Error "The request body could not be read."
        }

    let sourceDigest value =
        try
            value |> digestValue |> Ok
        with InvalidInput message ->
            Error message
