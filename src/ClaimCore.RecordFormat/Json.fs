namespace ClaimCore.RecordFormat

open System
open System.IO
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
            UTF8Encoding(false, true).GetByteCount(value) |> ignore
            value
        with
        | :? InvalidOperationException
        | :? EncoderFallbackException -> reject path "Malformed Unicode is not accepted."

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

    let integer path name element =
        let property = required path name element

        if property.ValueKind <> JsonValueKind.Number then
            reject (path + "." + name) "Expected an integer JSON number."

        match property.TryGetInt64() with
        | true, value -> value
        | _ ->
            reject
                (path + "." + name)
                "Expected a signed 64-bit integer without fraction or exponent."

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

    let encode write =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        stream.ToArray()

    let writeRegistration (writer: Utf8JsonWriter) (registration: RegistrationInput) =
        writer.WriteStartObject("registration")
        writer.WriteString("incidentDate", registration.IncidentDate)
        writer.WriteString("incidentNotificationDate", registration.IncidentNotificationDate)
        writer.WriteString("incidentCountry", registration.IncidentCountry)
        writer.WriteString("claimantName", registration.ClaimantName)
        writer.WriteString("insurerName", registration.InsurerName)
        writer.WriteString("claimedAmount", registration.ClaimedAmount)
        writer.WriteString("claimedCurrency", registration.ClaimedCurrency)
        writer.WriteEndObject()

    let writeDecision (writer: Utf8JsonWriter) (decision: DecisionInput) =
        writer.WriteStartObject("decision")
        writer.WriteString("paymentDecisionDate", decision.PaymentDecisionDate)
        writer.WriteString("payableAmount", decision.PayableAmount)
        writer.WriteString("payableCurrency", decision.PayableCurrency)
        writer.WriteEndObject()
