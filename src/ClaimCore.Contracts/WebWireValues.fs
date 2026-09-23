namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal WebWireValues =
    let private optional (writer: Utf8JsonWriter) (value: 'value option) (write: 'value -> unit) =
        match value with
        | Some item -> write item
        | None -> writer.WriteNullValue()

    let receipt (writer: Utf8JsonWriter) (value: OperationReceipt) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WritePropertyName("snapshot")
        CliWireValues.caseView writer value.Snapshot
        writer.WriteString("recordedAt", value.RecordedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("recordedBy", value.RecordedBy)
        writer.WriteBoolean("replayed", value.Replayed)
        writer.WriteString("command", WireTokens.command value.Command)
        writer.WriteEndObject()

    let changeSummary (writer: Utf8JsonWriter) (value: ChangeSummary) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WriteString("revision", value.Revision.ToString())
        writer.WriteString("command", WireTokens.command value.Command)
        writer.WriteString("recordedAt", value.RecordedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("recordedBy", value.RecordedBy)
        writer.WriteEndObject()

    let historyEntry (writer: Utf8JsonWriter) =
        function
        | HistoryEntry.SummaryEntry value ->
            writer.WriteStartObject()
            writer.WriteString("tag", "SUMMARY")
            writer.WritePropertyName("change")
            changeSummary writer value
            writer.WriteEndObject()
        | HistoryEntry.FullEntry value ->
            writer.WriteStartObject()
            writer.WriteString("tag", "FULL")
            writer.WritePropertyName("receipt")
            receipt writer value
            writer.WriteEndObject()

    let private authoredValue (writer: Utf8JsonWriter) (name: string, value: string) =
        writer.WriteStartObject()
        writer.WriteString("name", name)
        writer.WriteString("value", value)
        writer.WriteEndObject()

    let private attempt (writer: Utf8JsonWriter) (value: PreparationAttempt) =
        writer.WriteStartObject()
        writer.WriteString("attemptId", value.AttemptId)
        writer.WriteString("startedAt", value.StartedAt.ToUniversalTime().ToString("O"))
        writer.WritePropertyName("settlement")
        optional writer value.Settlement writer.WriteStringValue
        writer.WritePropertyName("settledAt")

        optional writer value.SettledAt (fun timestamp ->
            writer.WriteStringValue(timestamp.ToUniversalTime().ToString("O")))

        writer.WriteEndObject()

    let private attemptPage (writer: Utf8JsonWriter) (value: PreparationAttemptPage) =
        writer.WriteStartObject()
        writer.WritePropertyName("items")
        writer.WriteStartArray()
        value.Items |> List.iter (attempt writer)
        writer.WriteEndArray()
        writer.WritePropertyName("nextCursor")
        optional writer value.NextCursor writer.WriteStringValue

        writer.WriteEndObject()

    let preparationDetails (writer: Utf8JsonWriter) (value: PreparationDetails) =
        writer.WriteStartObject()
        writer.WritePropertyName("summary")
        CliWireValues.preparationSummary writer value.Summary
        writer.WriteString("expectedRevision", value.ExpectedVersion.ToString())
        writer.WritePropertyName("authoredValues")
        writer.WriteStartArray()
        value.AuthoredValues |> List.iter (authoredValue writer)
        writer.WriteEndArray()
        writer.WriteNumber("canonicalCommandFormat", value.CanonicalCommandFormat)
        writer.WriteString("preparingApplicationVersion", value.PreparingApplicationVersion)
        writer.WriteString("preparingContractFingerprint", value.PreparingContractFingerprint)
        writer.WriteString("preparingContractKind", value.PreparingContractKind)
        writer.WritePropertyName("attempts")
        attemptPage writer value.Attempts
        writer.WriteEndObject()

    let private fieldDiff (writer: Utf8JsonWriter) (value: FieldDiff) =
        writer.WriteStartObject()
        writer.WriteString("fieldName", value.FieldName)
        writer.WritePropertyName("before")
        optional writer value.Before writer.WriteStringValue
        writer.WritePropertyName("after")
        optional writer value.After writer.WriteStringValue
        writer.WriteEndObject()

    let review (writer: Utf8JsonWriter) (value: AdvisoryReview) =
        writer.WriteStartObject()
        writer.WritePropertyName("before")
        optional writer value.Before (CliWireValues.caseView writer)
        writer.WritePropertyName("proposed")
        CliWireValues.caseView writer value.Proposed
        writer.WritePropertyName("changes")
        writer.WriteStartArray()
        value.Changes |> List.iter (fieldDiff writer)
        writer.WriteEndArray()
        writer.WritePropertyName("context")
        writer.WriteStartObject()
        writer.WriteString("productVersion", value.Context.ProductVersion)

        writer.WriteString(
            "effectiveBusinessDate",
            value.Context.EffectiveBusinessDate.ToString("O")
        )

        writer.WriteString("timeZoneId", value.Context.TimeZoneId)
        writer.WriteEndObject()
        writer.WriteBoolean("advisory", value.IsAdvisory)
        writer.WriteEndObject()

    let semanticDefinition (writer: Utf8JsonWriter) (description: CoreDescription) =
        let projection = ContractProjection.create description.Contract
        let rendered = ContractRenderers.semantic projection |> CanonicalContract.bytes
        use document = JsonDocument.Parse(rendered)
        document.RootElement.WriteTo(writer)

    let importPreview (writer: Utf8JsonWriter) (value: RecoveryImportPreview) =
        writer.WriteStartObject()
        writer.WriteString("artifactKind", WireTokens.webArtifactKind value.ArtifactKind)
        writer.WriteString("sourceSha256", value.SourceSha256)
        writer.WritePropertyName("decodedEffect")
        writer.WriteStartObject()
        writer.WriteString("operationId", value.DecodedEffect.OperationId)
        writer.WriteString("caseReference", value.DecodedEffect.CaseReference)
        writer.WriteString("command", WireTokens.command value.DecodedEffect.Command)
        writer.WriteString("expectedRevision", value.DecodedEffect.ExpectedVersion.ToString())
        writer.WritePropertyName("authoredValues")
        writer.WriteStartArray()
        value.DecodedEffect.AuthoredValues |> List.iter (authoredValue writer)
        writer.WriteEndArray()
        writer.WriteNumber("canonicalCommandFormat", value.DecodedEffect.CanonicalCommandFormat)
        writer.WriteString("requestSha256", value.DecodedEffect.RequestSha256)
        writer.WriteEndObject()
        writer.WritePropertyName("existingPreparation")
        optional writer value.ExistingPreparation (CliWireValues.preparationSummary writer)
        writer.WriteEndObject()
