// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandPrepareResponseOutcomePreparationStateUnknownJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomePreparationStateUnknown =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar69.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandPrepareResponseOutcomePreparationStateUnknownDataJson.read)
                    value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandPrepareResponseOutcomePreparationStateUnknown)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar69.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomePreparationStateUnknownDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeJson =
    let read (value: JsonElement) : CommandPrepareResponseOutcome =
        match JsonRead.tag "tag" value with
        | "PREPARED" ->
            CommandPrepareResponseOutcome.Prepared(
                (CommandPrepareResponseOutcomePreparedJson.read) value
            )
        | "OBSERVED_ACCEPTED" ->
            CommandPrepareResponseOutcome.ObservedAccepted(
                (CommandPrepareResponseOutcomeObservedAcceptedJson.read) value
            )
        | "RETAINED_FOR_RECOVERY" ->
            CommandPrepareResponseOutcome.RetainedForRecovery(
                (CommandPrepareResponseOutcomeRetainedForRecoveryJson.read) value
            )
        | "REJECTED" ->
            CommandPrepareResponseOutcome.Rejected(
                (CommandPrepareResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            CommandPrepareResponseOutcome.Failed(
                (CommandPrepareResponseOutcomeFailedJson.read) value
            )
        | "CANCELLED_BEFORE_ADMISSION" ->
            CommandPrepareResponseOutcome.CancelledBeforeAdmission(
                (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.read) value
            )
        | "PREPARATION_STATE_UNKNOWN" ->
            CommandPrepareResponseOutcome.PreparationStateUnknown(
                (CommandPrepareResponseOutcomePreparationStateUnknownJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcome) =
        match value with
        | CommandPrepareResponseOutcome.Prepared item ->
            (CommandPrepareResponseOutcomePreparedJson.write) writer item
        | CommandPrepareResponseOutcome.ObservedAccepted item ->
            (CommandPrepareResponseOutcomeObservedAcceptedJson.write) writer item
        | CommandPrepareResponseOutcome.RetainedForRecovery item ->
            (CommandPrepareResponseOutcomeRetainedForRecoveryJson.write) writer item
        | CommandPrepareResponseOutcome.Rejected item ->
            (CommandPrepareResponseOutcomeRejectedJson.write) writer item
        | CommandPrepareResponseOutcome.Failed item ->
            (CommandPrepareResponseOutcomeFailedJson.write) writer item
        | CommandPrepareResponseOutcome.CancelledBeforeAdmission item ->
            (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.write) writer item
        | CommandPrepareResponseOutcome.PreparationStateUnknown item ->
            (CommandPrepareResponseOutcomePreparationStateUnknownJson.write) writer item

module internal CommandPrepareResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : CommandPrepareResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar64.read) value
            Outcome = JsonRead.required "outcome" (CommandPrepareResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar64.write) writer value.Endpoint
        JsonWrite.property "outcome" (CommandPrepareResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandOpenValuesJson =
    let private properties =
        [
            "incidentDate"
            "incidentNotificationDate"
            "incidentCountry"
            "claimantName"
            "insurerName"
            "claimedAmount"
            "claimedCurrency"
        ]

    let read (value: JsonElement) : CommandPrepareRequestCommandOpenValues =
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
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandOpenValues) =
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
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandOpenJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandOpen =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar70.read) value
            Values =
                JsonRead.required "values" (CommandPrepareRequestCommandOpenValuesJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandOpen) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar70.write) writer value.Kind

        JsonWrite.property
            "values"
            (CommandPrepareRequestCommandOpenValuesJson.write)
            writer
            value.Values

        writer.WriteEndObject()

module internal CommandPrepareRequestCommandAmendRegistrationJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandAmendRegistration =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar71.read) value
            Values =
                JsonRead.required "values" (CommandPrepareRequestCommandOpenValuesJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandAmendRegistration) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar71.write) writer value.Kind

        JsonWrite.property
            "values"
            (CommandPrepareRequestCommandOpenValuesJson.write)
            writer
            value.Values

        writer.WriteEndObject()

module internal CommandPrepareRequestCommandDecideValuesJson =
    let private properties =
        [ "paymentDecisionDate"; "payableAmount"; "payableCurrency" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandDecideValues =
        JsonRead.objectValue properties value

        {
            PaymentDecisionDate =
                JsonRead.required "paymentDecisionDate" (ProtocolScalar0.read) value
            PayableAmount = JsonRead.required "payableAmount" (ProtocolScalar3.read) value
            PayableCurrency = JsonRead.required "payableCurrency" (ProtocolScalar4.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandDecideValues) =
        writer.WriteStartObject()

        JsonWrite.property
            "paymentDecisionDate"
            (ProtocolScalar0.write)
            writer
            value.PaymentDecisionDate

        JsonWrite.property "payableAmount" (ProtocolScalar3.write) writer value.PayableAmount
        JsonWrite.property "payableCurrency" (ProtocolScalar4.write) writer value.PayableCurrency
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandDecideJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandDecide =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar72.read) value
            Values =
                JsonRead.required "values" (CommandPrepareRequestCommandDecideValuesJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandDecide) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar72.write) writer value.Kind

        JsonWrite.property
            "values"
            (CommandPrepareRequestCommandDecideValuesJson.write)
            writer
            value.Values

        writer.WriteEndObject()

module internal CommandPrepareRequestCommandWithdrawDecisionJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandWithdrawDecision =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar73.read) value
            Values = JsonRead.required "values" (SessionLogoutRequestJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandWithdrawDecision) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar73.write) writer value.Kind
        JsonWrite.property "values" (SessionLogoutRequestJson.write) writer value.Values
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandRecordPaymentValuesJson =
    let private properties = [ "paymentDate" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandRecordPaymentValues =
        JsonRead.objectValue properties value

        {
            PaymentDate = JsonRead.required "paymentDate" (ProtocolScalar0.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandRecordPaymentValues) =
        writer.WriteStartObject()
        JsonWrite.property "paymentDate" (ProtocolScalar0.write) writer value.PaymentDate
        writer.WriteEndObject()
