namespace ClaimCore.RecordFormat

open ClaimCore.Contract

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

    let properties path expected element =
        match element with
        | JsonValue.Object members when Set.ofList (List.map fst members) = Set.ofList expected ->
            ()
        | _ -> reject path "Expected exactly the record members."

    let required path name element =
        JsonValue.tryMember name element
        |> Option.defaultWith (fun () -> reject path "A required property is missing.")

    let stringValue path =
        function
        | JsonValue.String value -> persistedString path value
        | _ -> reject path "Expected a persisted string."

    let text path name element =
        required path name element |> stringValue (path + "." + name)

    let guid path name element =
        let value = text path name element

        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> reject (path + "." + name) "Use a canonical non-empty UUID."

    let boolean path name element =
        match required path name element with
        | JsonValue.Boolean value -> value
        | _ -> reject (path + "." + name) "Expected a boolean."

    let integer path name element =
        match required path name element with
        | JsonValue.Integer value -> value
        | _ -> reject (path + "." + name) "Expected a safe integer."

    let optionalString path name element =
        match required path name element with
        | JsonValue.Null -> None
        | value -> Some(stringValue (path + "." + name) value)

    let registration path element : Registration =
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

    let decision path element : Decision =
        properties path [ "paymentDecisionDate"; "payableAmount"; "payableCurrency" ] element

        {
            PaymentDecisionDate = text path "paymentDecisionDate" element
            PayableAmount = text path "payableAmount" element
            PayableCurrency = text path "payableCurrency" element
        }

    let bounded maxBytes bytes read =
        StrictJson.read maxBytes bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind (fun value ->
            try
                Ok(read value)
            with DecodeError(field, message) ->
                Error { Field = field; Message = message })

    let dispatch decoders root =
        let format = integer "$" "format" root

        match decoders |> List.tryFind (fst >> (=) format) with
        | Some(_, read) -> read root
        | None -> reject "$.format" "Unsupported record format."

    let registrationValue (registration: Registration) =
        JsonValue.Object
            [
                "incidentDate", JsonValue.String registration.IncidentDate
                "incidentNotificationDate", JsonValue.String registration.IncidentNotificationDate
                "incidentCountry", JsonValue.String registration.IncidentCountry
                "claimantName", JsonValue.String registration.ClaimantName
                "insurerName", JsonValue.String registration.InsurerName
                "claimedAmount", JsonValue.String registration.ClaimedAmount
                "claimedCurrency", JsonValue.String registration.ClaimedCurrency
            ]

    let decisionValue (decision: Decision) =
        JsonValue.Object
            [
                "paymentDecisionDate", JsonValue.String decision.PaymentDecisionDate
                "payableAmount", JsonValue.String decision.PayableAmount
                "payableCurrency", JsonValue.String decision.PayableCurrency
            ]
