namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal CliWirePreparation =
    let private review (writer: Utf8JsonWriter) (value: AdvisoryReview) =
        writer.WriteStartObject()
        writer.WritePropertyName("before")

        match value.Before with
        | Some before -> CliWireValues.caseView writer before
        | None -> writer.WriteNullValue()

        writer.WritePropertyName("proposed")
        CliWireValues.caseView writer value.Proposed
        writer.WriteStartArray("fieldDiff")

        value.Changes
        |> List.iter (fun change ->
            writer.WriteStartObject()
            writer.WriteString("fieldName", change.FieldName)

            match change.Before with
            | Some before -> writer.WriteString("before", before)
            | None -> writer.WriteNull("before")

            match change.After with
            | Some after -> writer.WriteString("after", after)
            | None -> writer.WriteNull("after")

            writer.WriteEndObject())

        writer.WriteEndArray()
        writer.WriteStartObject("context")
        writer.WriteString("productVersion", value.Context.ProductVersion)

        writer.WriteString(
            "effectiveBusinessDate",
            value.Context.EffectiveBusinessDate.ToString("O")
        )

        writer.WriteString("timeZoneId", value.Context.TimeZoneId)
        writer.WriteEndObject()
        writer.WriteBoolean("advisory", value.IsAdvisory)
        writer.WriteEndObject()

    let private accepted (writer: Utf8JsonWriter) details receipt =
        writer.WriteStartObject()
        writer.WriteString("kind", "observedAccepted")
        writer.WritePropertyName("details")
        CliWireValues.preparationDetails writer details
        writer.WritePropertyName("receipt")
        CliWireValues.receipt false writer receipt
        writer.WriteEndObject()

    let private retained (writer: Utf8JsonWriter) details rejection =
        writer.WriteStartObject()
        writer.WriteString("kind", "retainedForRecovery")
        writer.WritePropertyName("details")
        CliWireValues.preparationDetails writer details
        writer.WritePropertyName("rejection")
        CliWireValues.rejection writer rejection
        writer.WriteEndObject()

    let prepare (writer: Utf8JsonWriter) (outcome: PrepareOutcome) =
        match outcome with
        | PrepareOutcome.Prepared(details, advisory) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "prepared")
            writer.WritePropertyName("details")
            CliWireValues.preparationDetails writer details
            writer.WritePropertyName("review")
            review writer advisory
            writer.WriteEndObject()
        | PrepareOutcome.ObservedAccepted(details, receipt) -> accepted writer details receipt
        | PrepareOutcome.RetainedForRecovery(details, rejection) ->
            retained writer details rejection
        | PrepareOutcome.PrepareRejected(operationId, rejection) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("rejection")
            CliWireValues.rejection writer rejection
            writer.WriteEndObject()
        | PrepareOutcome.PrepareFailed(operationId, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "failed")
            writer.WriteString("operationId", operationId)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()
        | PrepareOutcome.CancelledBeforeAdmission operationId ->
            writer.WriteStartObject()
            writer.WriteString("kind", "cancelledBeforeAdmission")
            writer.WriteString("operationId", operationId)
            writer.WriteEndObject()
        | PrepareOutcome.PreparationStateUnknown(operationId, digest, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "preparationStateUnknown")
            writer.WriteString("operationId", operationId)
            writer.WriteString("requestSha256", digest)
            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()
