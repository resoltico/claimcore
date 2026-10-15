namespace ClaimCore.RecordFormat

open System
open System.Text
open System.Text.Json
open ClaimCore.Domain

/// A wire failure never contains the raw request or a framework exception message.
type DecodeProblem = { Field: string; Message: string }

exception internal DecodeError of field: string * message: string

module internal Json =
    let reject path message = raise (DecodeError(path, message))

    let private decodedText path read =
        try
            let value: string = read ()

            if not (UnicodeText.isPersisted value) then
                reject path "Persisted text requires valid Unicode without NUL or noncharacters."

            value
        with
        | :? InvalidOperationException
        | :? EncoderFallbackException -> reject path "Malformed Unicode is not accepted."

    let persistedString path value = decodedText path (fun () -> value)

    let properties path expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            reject path "Expected an object."

        let actual =
            element.EnumerateObject()
            |> Seq.map (fun property -> decodedText path (fun () -> property.Name))
            |> Seq.toList

        if (Set.ofList actual).Count <> actual.Length then
            reject path "Duplicate object properties are not accepted."

        if actual |> List.exists (fun name -> not (List.contains name expected)) then
            reject path "The object contains an unknown property."

        if expected |> List.exists (fun name -> not (List.contains name actual)) then
            reject path "A required property is missing."

    let required path (name: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            reject path "Expected an object."

        match element.TryGetProperty(name) with
        | true, property -> property
        | _ -> reject path "A required property is missing."

    let stringValue path (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            reject path "Expected a string, not a number, null, or another JSON type."

        decodedText path (fun () ->
            element.GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> reject path "Null is not accepted."))

    let text path name element =
        required path name element |> stringValue (path + "." + name)

    let guid path name element =
        let value = text path name element

        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ ->
            reject
                (path + "." + name)
                "Use one non-empty UUID in canonical lowercase hyphenated form."

    let boolean path name element =
        let value = required path name element

        match value.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> reject (path + "." + name) "Expected a JSON boolean."

    let integer path name element =
        let property = required path name element

        if property.ValueKind <> JsonValueKind.Number then
            reject (path + "." + name) "Expected an integer JSON number."

        match property.TryGetInt64() with
        | true, value when
            value >= -CanonicalJson.MaximumInteger && value <= CanonicalJson.MaximumInteger
            ->
            value
        | _ -> reject (path + "." + name) "Expected a safe integer without fraction or exponent."

    let optionalString path name element =
        let property = required path name element

        if property.ValueKind = JsonValueKind.Null then
            None
        else
            Some(stringValue (path + "." + name) property)

    let registration path element : RegistrationInput =
        properties
            path
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
            ]
            element

        {
            IncidentDate = text path "incidentDate" element
            IncidentNotificationDate = text path "incidentNotificationDate" element
            IncidentCountry = text path "incidentCountry" element
            ClaimantName = text path "claimantName" element
            InsurerName = text path "insurerName" element
            ClaimedAmount = text path "claimedAmount" element
            ClaimedCurrency = text path "claimedCurrency" element
        }

    let decision path element : DecisionInput =
        properties path [ "paymentDecisionDate"; "payableAmount"; "payableCurrency" ] element

        {
            PaymentDecisionDate = text path "paymentDecisionDate" element
            PayableAmount = text path "payableAmount" element
            PayableCurrency = text path "payableCurrency" element
        }

    let parse (bytes: byte array) read =
        try
            UTF8Encoding(false, true).GetCharCount(bytes) |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 32))

            Ok(read document.RootElement)
        with
        | DecodeError(field, message) -> Error { Field = field; Message = message }
        | :? DecoderFallbackException ->
            Error
                {
                    Field = "$"
                    Message = "The request must be valid UTF-8."
                }
        | :? JsonException ->
            Error
                {
                    Field = "$"
                    Message =
                        "Malformed JSON. Comments, trailing data, and invalid UTF-8 are not accepted."
                }

    let bounded maxBytes (bytes: byte array) read =
        if obj.ReferenceEquals(bytes, null) || bytes.Length > maxBytes then
            Error
                {
                    Field = "$"
                    Message = "Missing record or record exceeds its byte limit."
                }
        else
            parse bytes read

    let dispatch decoders root =
        let format = integer "$" "format" root

        match decoders |> List.tryFind (fst >> (=) format) with
        | Some(_, read) -> read root
        | None -> reject "$.format" "Unsupported record format."

    let registrationValue (registration: RegistrationInput) =
        CanonicalValue.Object
            [
                "incidentDate", CanonicalValue.String registration.IncidentDate
                "incidentNotificationDate",
                CanonicalValue.String registration.IncidentNotificationDate
                "incidentCountry", CanonicalValue.String registration.IncidentCountry
                "claimantName", CanonicalValue.String registration.ClaimantName
                "insurerName", CanonicalValue.String registration.InsurerName
                "claimedAmount", CanonicalValue.String registration.ClaimedAmount
                "claimedCurrency", CanonicalValue.String registration.ClaimedCurrency
            ]

    let decisionValue (decision: DecisionInput) =
        CanonicalValue.Object
            [
                "paymentDecisionDate", CanonicalValue.String decision.PaymentDecisionDate
                "payableAmount", CanonicalValue.String decision.PayableAmount
                "payableCurrency", CanonicalValue.String decision.PayableCurrency
            ]
