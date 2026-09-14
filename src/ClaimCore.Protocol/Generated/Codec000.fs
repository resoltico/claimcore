// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CaseFieldsJson =
    let private properties =
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

    let read (value: JsonElement) : CaseFields =
        JsonRead.objectValue properties value

        {
            IncidentDate = JsonRead.required "incidentDate" (ProtocolScalar0.read) value
            IncidentNotificationDate =
                JsonRead.required "incidentNotificationDate" (ProtocolScalar0.read) value
            IncidentCountry = JsonRead.required "incidentCountry" (ProtocolScalar1.read) value
            ClaimantName = JsonRead.required "claimantName" (ProtocolScalar2.read) value
            InsurerName = JsonRead.required "insurerName" (ProtocolScalar2.read) value
            ClaimedAmount = JsonRead.required "claimedAmount" (ProtocolScalar3.read) value
            ClaimedCurrency = JsonRead.required "claimedCurrency" (ProtocolScalar4.read) value
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar5.read) value
            PaymentDecisionDate =
                JsonRead.required
                    "paymentDecisionDate"
                    (JsonRead.nullable (ProtocolScalar0.read))
                    value
            PayableAmount =
                JsonRead.required "payableAmount" (JsonRead.nullable (ProtocolScalar3.read)) value
            PayableCurrency =
                JsonRead.required "payableCurrency" (JsonRead.nullable (ProtocolScalar4.read)) value
            PaymentDate =
                JsonRead.required "paymentDate" (JsonRead.nullable (ProtocolScalar0.read)) value
            Status = JsonRead.required "status" (ProtocolScalar6.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseFields) =
        writer.WriteStartObject()
        JsonWrite.property "incidentDate" (ProtocolScalar0.write) writer value.IncidentDate

        JsonWrite.property
            "incidentNotificationDate"
            (ProtocolScalar0.write)
            writer
            value.IncidentNotificationDate

        JsonWrite.property "incidentCountry" (ProtocolScalar1.write) writer value.IncidentCountry
        JsonWrite.property "claimantName" (ProtocolScalar2.write) writer value.ClaimantName
        JsonWrite.property "insurerName" (ProtocolScalar2.write) writer value.InsurerName
        JsonWrite.property "claimedAmount" (ProtocolScalar3.write) writer value.ClaimedAmount
        JsonWrite.property "claimedCurrency" (ProtocolScalar4.write) writer value.ClaimedCurrency
        JsonWrite.property "caseReference" (ProtocolScalar5.write) writer value.CaseReference

        JsonWrite.property
            "paymentDecisionDate"
            (JsonWrite.nullable (ProtocolScalar0.write))
            writer
            value.PaymentDecisionDate

        JsonWrite.property
            "payableAmount"
            (JsonWrite.nullable (ProtocolScalar3.write))
            writer
            value.PayableAmount

        JsonWrite.property
            "payableCurrency"
            (JsonWrite.nullable (ProtocolScalar4.write))
            writer
            value.PayableCurrency

        JsonWrite.property
            "paymentDate"
            (JsonWrite.nullable (ProtocolScalar0.write))
            writer
            value.PaymentDate

        JsonWrite.property "status" (ProtocolScalar6.write) writer value.Status
        writer.WriteEndObject()

module internal CaseViewJson =
    let private properties = [ "fields"; "revision" ]

    let read (value: JsonElement) : CaseView =
        JsonRead.objectValue properties value

        {
            Fields = JsonRead.required "fields" (CaseFieldsJson.read) value
            Revision = JsonRead.required "revision" (ProtocolScalar7.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseView) =
        writer.WriteStartObject()
        JsonWrite.property "fields" (CaseFieldsJson.write) writer value.Fields
        JsonWrite.property "revision" (ProtocolScalar7.write) writer value.Revision
        writer.WriteEndObject()

module internal FieldDiffJson =
    let private properties = [ "fieldName"; "before"; "after" ]

    let read (value: JsonElement) : FieldDiff =
        JsonRead.objectValue properties value

        {
            FieldName = JsonRead.required "fieldName" (ProtocolScalar8.read) value
            Before = JsonRead.required "before" (JsonRead.nullable (ProtocolScalar8.read)) value
            After = JsonRead.required "after" (JsonRead.nullable (ProtocolScalar8.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: FieldDiff) =
        writer.WriteStartObject()
        JsonWrite.property "fieldName" (ProtocolScalar8.write) writer value.FieldName
        JsonWrite.property "before" (JsonWrite.nullable (ProtocolScalar8.write)) writer value.Before
        JsonWrite.property "after" (JsonWrite.nullable (ProtocolScalar8.write)) writer value.After
        writer.WriteEndObject()

module internal RuntimeContextJson =
    let private properties = [ "productVersion"; "effectiveBusinessDate"; "timeZoneId" ]

    let read (value: JsonElement) : RuntimeContext =
        JsonRead.objectValue properties value

        {
            ProductVersion = JsonRead.required "productVersion" (ProtocolScalar8.read) value
            EffectiveBusinessDate =
                JsonRead.required "effectiveBusinessDate" (ProtocolScalar0.read) value
            TimeZoneId = JsonRead.required "timeZoneId" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RuntimeContext) =
        writer.WriteStartObject()
        JsonWrite.property "productVersion" (ProtocolScalar8.write) writer value.ProductVersion

        JsonWrite.property
            "effectiveBusinessDate"
            (ProtocolScalar0.write)
            writer
            value.EffectiveBusinessDate

        JsonWrite.property "timeZoneId" (ProtocolScalar8.write) writer value.TimeZoneId
        writer.WriteEndObject()

module internal AdvisoryReviewJson =
    let private properties = [ "before"; "proposed"; "changes"; "context"; "advisory" ]

    let read (value: JsonElement) : AdvisoryReview =
        JsonRead.objectValue properties value

        {
            Before = JsonRead.required "before" (JsonRead.nullable (CaseViewJson.read)) value
            Proposed = JsonRead.required "proposed" (CaseViewJson.read) value
            Changes =
                JsonRead.required "changes" (JsonRead.array None None (FieldDiffJson.read)) value
            Context = JsonRead.required "context" (RuntimeContextJson.read) value
            Advisory = JsonRead.required "advisory" (ProtocolScalar9.read) value
        }

    let write (writer: Utf8JsonWriter) (value: AdvisoryReview) =
        writer.WriteStartObject()
        JsonWrite.property "before" (JsonWrite.nullable (CaseViewJson.write)) writer value.Before
        JsonWrite.property "proposed" (CaseViewJson.write) writer value.Proposed
        JsonWrite.property "changes" (JsonWrite.array (FieldDiffJson.write)) writer value.Changes
        JsonWrite.property "context" (RuntimeContextJson.write) writer value.Context
        JsonWrite.property "advisory" (ProtocolScalar9.write) writer value.Advisory
        writer.WriteEndObject()

module internal CaseSummaryJson =
    let private properties = [ "caseReference"; "revision"; "status" ]

    let read (value: JsonElement) : CaseSummary =
        JsonRead.objectValue properties value

        {
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar8.read) value
            Revision = JsonRead.required "revision" (ProtocolScalar7.read) value
            Status = JsonRead.required "status" (ProtocolScalar6.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseSummary) =
        writer.WriteStartObject()
        JsonWrite.property "caseReference" (ProtocolScalar8.write) writer value.CaseReference
        JsonWrite.property "revision" (ProtocolScalar7.write) writer value.Revision
        JsonWrite.property "status" (ProtocolScalar6.write) writer value.Status
        writer.WriteEndObject()

module internal ChangeSummaryJson =
    let private properties =
        [ "operationId"; "revision"; "command"; "recordedAt"; "recordedBy" ]

    let read (value: JsonElement) : ChangeSummary =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Revision = JsonRead.required "revision" (ProtocolScalar7.read) value
            Command = JsonRead.required "command" (ProtocolScalar11.read) value
            RecordedAt = JsonRead.required "recordedAt" (ProtocolScalar12.read) value
            RecordedBy = JsonRead.required "recordedBy" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: ChangeSummary) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "revision" (ProtocolScalar7.write) writer value.Revision
        JsonWrite.property "command" (ProtocolScalar11.write) writer value.Command
        JsonWrite.property "recordedAt" (ProtocolScalar12.write) writer value.RecordedAt
        JsonWrite.property "recordedBy" (ProtocolScalar8.write) writer value.RecordedBy
        writer.WriteEndObject()

module internal CommandInputDescriptorBlankJson =
    let private properties = [ "fieldName"; "prefill" ]

    let read (value: JsonElement) : CommandInputDescriptorBlank =
        JsonRead.objectValue properties value

        {
            FieldName = JsonRead.required "fieldName" (ProtocolScalar8.read) value
            Prefill = JsonRead.required "prefill" (ProtocolScalar13.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandInputDescriptorBlank) =
        writer.WriteStartObject()
        JsonWrite.property "fieldName" (ProtocolScalar8.write) writer value.FieldName
        JsonWrite.property "prefill" (ProtocolScalar13.write) writer value.Prefill
        writer.WriteEndObject()

module internal CommandInputDescriptorCurrentFieldJson =
    let private properties = [ "fieldName"; "prefill"; "currentField" ]

    let read (value: JsonElement) : CommandInputDescriptorCurrentField =
        JsonRead.objectValue properties value

        {
            FieldName = JsonRead.required "fieldName" (ProtocolScalar8.read) value
            Prefill = JsonRead.required "prefill" (ProtocolScalar14.read) value
            CurrentField = JsonRead.required "currentField" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandInputDescriptorCurrentField) =
        writer.WriteStartObject()
        JsonWrite.property "fieldName" (ProtocolScalar8.write) writer value.FieldName
        JsonWrite.property "prefill" (ProtocolScalar14.write) writer value.Prefill
        JsonWrite.property "currentField" (ProtocolScalar8.write) writer value.CurrentField
        writer.WriteEndObject()

module internal CommandInputDescriptorJson =
    let read (value: JsonElement) : CommandInputDescriptor =
        match JsonRead.tag "prefill" value with
        | "BLANK" -> CommandInputDescriptor.Blank((CommandInputDescriptorBlankJson.read) value)
        | "CURRENT_FIELD" ->
            CommandInputDescriptor.CurrentField((CommandInputDescriptorCurrentFieldJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CommandInputDescriptor) =
        match value with
        | CommandInputDescriptor.Blank item -> (CommandInputDescriptorBlankJson.write) writer item
        | CommandInputDescriptor.CurrentField item ->
            (CommandInputDescriptorCurrentFieldJson.write) writer item
