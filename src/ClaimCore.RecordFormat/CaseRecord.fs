namespace ClaimCore.RecordFormat

open System.Text.Json
open ClaimCore.Domain

module CaseRecord =
    let private optionalText (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    /// This object contains exactly the thirteen requested entries; no workflow/audit metadata.
    let writeFields (writer: Utf8JsonWriter) (fields: CaseFields) =
        writer.WriteStartObject()
        writer.WriteString("incidentDate", fields.IncidentDate)
        writer.WriteString("incidentNotificationDate", fields.IncidentNotificationDate)
        writer.WriteString("incidentCountry", fields.IncidentCountry)
        writer.WriteString("claimantName", fields.ClaimantName)
        writer.WriteString("insurerName", fields.InsurerName)
        writer.WriteString("claimedAmount", fields.ClaimedAmount)
        writer.WriteString("claimedCurrency", fields.ClaimedCurrency)
        writer.WriteString("caseReference", fields.CaseReference)
        optionalText writer "paymentDecisionDate" fields.PaymentDecisionDate
        optionalText writer "payableAmount" fields.PayableAmount
        optionalText writer "payableCurrency" fields.PayableCurrency
        optionalText writer "paymentDate" fields.PaymentDate
        writer.WriteString("status", CaseStatuses.token fields.Status)
        writer.WriteEndObject()

    let encodeSnapshot (snapshot: CaseView) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WritePropertyName("fields")
            writeFields writer snapshot.Fields
            writer.WriteNumber("version", snapshot.Version)
            writer.WriteEndObject())

    let private readFields root : CaseFields =
        let path = "$.fields"

        Json.properties
            path
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
                "caseReference"
                "paymentDecisionDate"
                "payableAmount"
                "payableCurrency"
                "paymentDate"
                "status"
            ]
            root

        let status =
            match Json.text path "status" root |> CaseStatuses.tryParse with
            | Some value -> value
            | None -> Json.reject "$.fields.status" "Unknown persisted case status."

        {
            IncidentDate = Json.text path "incidentDate" root
            IncidentNotificationDate = Json.text path "incidentNotificationDate" root
            IncidentCountry = Json.text path "incidentCountry" root
            ClaimantName = Json.text path "claimantName" root
            InsurerName = Json.text path "insurerName" root
            ClaimedAmount = Json.text path "claimedAmount" root
            ClaimedCurrency = Json.text path "claimedCurrency" root
            CaseReference = Json.text path "caseReference" root
            PaymentDecisionDate = Json.optionalString path "paymentDecisionDate" root
            PayableAmount = Json.optionalString path "payableAmount" root
            PayableCurrency = Json.optionalString path "payableCurrency" root
            PaymentDate = Json.optionalString path "paymentDate" root
            Status = status
        }

    /// Snapshot encoding version 2 is a deliberate break from the previous nested field model.
    let decodeSnapshot bytes =
        Json.parse bytes (fun root ->
            Json.properties "$" [ "fields"; "version" ] root

            {
                Fields = Json.required "$" "fields" root |> readFields
                Version = Json.integer "$" "version" root
            })
