namespace ClaimCore.Web

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type LoginInput =
    {
        Credential: string
        AntiforgeryToken: string
    }

[<NoEquality; NoComparison>]
type PageInput = { Cursor: string option; Limit: int }

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

/// Strict HTTP-v2 request decoding. The request schema comes from ClaimCore.Contracts; this module
/// enforces exact JSON object semantics before values reach the typed Application facade.
module HttpInput =
    exception private InvalidInput of string

    let private fail message = raise (InvalidInput message)

    let private properties (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail "Expected a JSON object."

        let values = element.EnumerateObject() |> Seq.toList
        let names = values |> List.map _.Name

        if names.Length <> (names |> Set.ofList |> Set.count) then
            fail "Duplicate JSON properties are not accepted."

        values |> List.map (fun property -> property.Name, property.Value) |> Map.ofList

    let private exactProperties expected values =
        let names = values |> Map.toList |> List.map fst |> Set.ofList
        let allowed = expected |> Set.ofList

        if not (Set.isSubset names allowed) then
            fail "The JSON object contains an unknown property."

        values

    let private required name values =
        values
        |> Map.tryFind name
        |> Option.defaultWith (fun () -> fail "A required JSON property is missing.")

    let private stringValue (property: JsonElement) =
        if property.ValueKind <> JsonValueKind.String then
            fail "Expected a JSON string."

        property.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> fail "JSON null is not accepted here.")

    let private optionalString name values =
        values |> Map.tryFind name |> Option.map stringValue

    let private boolValue (property: JsonElement) =
        if
            property.ValueKind <> JsonValueKind.True
            && property.ValueKind <> JsonValueKind.False
        then
            fail "Expected a JSON boolean."

        property.GetBoolean()

    let private integerValue (property: JsonElement) =
        let mutable value = 0

        if property.ValueKind <> JsonValueKind.Number || not (property.TryGetInt32(&value)) then
            fail "Expected a JSON integer."

        value

    let private canonicalNonNegativeInt64 (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when
            parsed < Int64.MaxValue && parsed.ToString(CultureInfo.InvariantCulture) = value
            ->
            parsed
        | _ -> fail "Expected a canonical non-negative revision below Int64.MaxValue."

    let private operationIdValue (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> fail "Use one non-empty canonical lowercase UUID."

    let private digestValue (value: string) =
        let validCharacter character =
            ('0' <= character && character <= '9') || ('a' <= character && character <= 'f')

        if value.Length <> 64 || not (value |> Seq.forall validCharacter) then
            fail "Use one lowercase SHA-256 digest."

        value

    let private commandKind value =
        CommandKinds.all
        |> List.tryFind (fun kind -> CommandKinds.token kind = value)
        |> Option.defaultWith (fun () -> fail "The command token is not supported.")

    let private commandDraft (root: JsonElement) =
        let values =
            properties root
            |> exactProperties [ "operationId"; "caseReference"; "expectedRevision"; "command" ]

        let command = required "command" values |> properties
        let kind = required "kind" command |> stringValue |> commandKind
        let commandValues = required "values" command |> properties

        let expectedNames =
            CommandDefinitions.forKind kind
            |> fun definition -> definition.Inputs |> List.map _.FieldName

        exactProperties [ "kind"; "values" ] command |> ignore
        exactProperties expectedNames commandValues |> ignore

        if expectedNames |> List.exists (fun name -> not (commandValues.ContainsKey name)) then
            fail "A required command value is missing."

        {
            OperationId = required "operationId" values |> stringValue |> operationIdValue
            CaseReference = required "caseReference" values |> stringValue
            ExpectedVersion =
                required "expectedRevision" values |> stringValue |> canonicalNonNegativeInt64
            Kind = kind
            Values =
                expectedNames |> List.map (fun name -> name, commandValues[name] |> stringValue)
        }

    let private parse (bytes: byte array) read =
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

    let login bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "credential"; "antiforgeryToken" ]

            {
                Credential = required "credential" values |> stringValue
                AntiforgeryToken = required "antiforgeryToken" values |> stringValue
            })

    let logout bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties []

            if not values.IsEmpty then
                fail "Logout requires an empty JSON object.")

    let draft bytes = parse bytes commandDraft

    let caseReference bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "caseReference" ]
            required "caseReference" values |> stringValue)

    let page maximum bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "cursor"; "limit" ]
            let limit = required "limit" values |> integerValue

            if limit < 1 || limit > maximum then
                fail "The requested page size is outside the supported range."

            {
                Cursor = optionalString "cursor" values
                Limit = limit
            })

    let history maximum bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties [ "caseReference"; "cursor"; "limit"; "detail" ]

            let limit = required "limit" values |> integerValue

            if limit < 1 || limit > maximum then
                fail "The requested page size is outside the supported range."

            let detail =
                match required "detail" values |> stringValue with
                | "SUMMARY" -> HistoryDetail.Summary
                | "FULL" -> HistoryDetail.Full
                | _ -> fail "The history detail is not supported."

            {
                CaseReference = required "caseReference" values |> stringValue
                Cursor = optionalString "cursor" values
                Limit = limit
                Detail = detail
            })

    let operationId bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "operationId" ]
            required "operationId" values |> stringValue |> operationIdValue)

    let resolve bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "operationId"; "requestSha256" ]

            {
                OperationId = required "operationId" values |> stringValue |> operationIdValue
                RequestSha256 = required "requestSha256" values |> stringValue |> digestValue
            })

    let dismiss bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties [ "operationId"; "requestSha256"; "confirmed" ]

            {
                OperationId = required "operationId" values |> stringValue |> operationIdValue
                RequestSha256 = required "requestSha256" values |> stringValue |> digestValue
                Confirmed = required "confirmed" values |> boolValue
            })

    let sourceDigest value =
        try
            value |> digestValue |> Ok
        with InvalidInput message ->
            Error message
