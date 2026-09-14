// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal RecoveryExportResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryExportResponseOutcome =
        match JsonRead.tag "tag" value with
        | "NOT_FOUND" ->
            RecoveryExportResponseOutcome.NotFound(
                (RecoveryDismissResponseOutcomeNotFoundJson.read) value
            )
        | "REJECTED" ->
            RecoveryExportResponseOutcome.Rejected(
                (RecoveryListResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            RecoveryExportResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            RecoveryExportResponseOutcome.Cancelled(
                (CaseGetResponseOutcomeCancelledJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryExportResponseOutcome) =
        match value with
        | RecoveryExportResponseOutcome.NotFound item ->
            (RecoveryDismissResponseOutcomeNotFoundJson.write) writer item
        | RecoveryExportResponseOutcome.Rejected item ->
            (RecoveryListResponseOutcomeRejectedJson.write) writer item
        | RecoveryExportResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryExportResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal RecoveryExportResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryExportResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar95.read) value
            Outcome = JsonRead.required "outcome" (RecoveryExportResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryExportResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar95.write) writer value.Endpoint
        JsonWrite.property "outcome" (RecoveryExportResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal RecoveryImportEnvelopePreviewResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryImportEnvelopePreviewResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data = JsonRead.required "data" (RecoveryImportPreviewJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopePreviewResponseOutcomeSucceeded)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag
        JsonWrite.property "data" (RecoveryImportPreviewJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryImportEnvelopePreviewResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryImportEnvelopePreviewResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            RecoveryImportEnvelopePreviewResponseOutcome.Succeeded(
                (RecoveryImportEnvelopePreviewResponseOutcomeSucceededJson.read) value
            )
        | "REJECTED" ->
            RecoveryImportEnvelopePreviewResponseOutcome.Rejected(
                (RecoveryListResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            RecoveryImportEnvelopePreviewResponseOutcome.Failed(
                (CaseGetResponseOutcomeFailedJson.read) value
            )
        | "CANCELLED" ->
            RecoveryImportEnvelopePreviewResponseOutcome.Cancelled(
                (CaseGetResponseOutcomeCancelledJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryImportEnvelopePreviewResponseOutcome) =
        match value with
        | RecoveryImportEnvelopePreviewResponseOutcome.Succeeded item ->
            (RecoveryImportEnvelopePreviewResponseOutcomeSucceededJson.write) writer item
        | RecoveryImportEnvelopePreviewResponseOutcome.Rejected item ->
            (RecoveryListResponseOutcomeRejectedJson.write) writer item
        | RecoveryImportEnvelopePreviewResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryImportEnvelopePreviewResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal RecoveryImportEnvelopePreviewResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryImportEnvelopePreviewResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar96.read) value
            Outcome =
                JsonRead.required
                    "outcome"
                    (RecoveryImportEnvelopePreviewResponseOutcomeJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportEnvelopePreviewResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar96.write) writer value.Endpoint

        JsonWrite.property
            "outcome"
            (RecoveryImportEnvelopePreviewResponseOutcomeJson.write)
            writer
            value.Outcome

        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainResponseOutcomeRetainedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryImportEnvelopeRetainResponseOutcomeRetained =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar98.read) value
            Data = JsonRead.required "data" (PreparationDetailsJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopeRetainResponseOutcomeRetained)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar98.write) writer value.Tag
        JsonWrite.property "data" (PreparationDetailsJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainResponseOutcomeExistingJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryImportEnvelopeRetainResponseOutcomeExisting =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar99.read) value
            Data = JsonRead.required "data" (PreparationDetailsJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopeRetainResponseOutcomeExisting)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar99.write) writer value.Tag
        JsonWrite.property "data" (PreparationDetailsJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmissionJson =
    let private properties = [ "tag"; "data" ]

    let read
        (value: JsonElement)
        : RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmission =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar68.read) value
            Data = JsonRead.required "data" (ProtocolScalar58.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmission)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar68.write) writer value.Tag
        JsonWrite.property "data" (ProtocolScalar58.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownDataJson =
    let private properties = [ "artifactKind"; "sourceSha256"; "operationId"; "fault" ]

    let read
        (value: JsonElement)
        : RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownData =
        JsonRead.objectValue properties value

        {
            ArtifactKind = JsonRead.required "artifactKind" (ProtocolScalar46.read) value
            SourceSha256 = JsonRead.required "sourceSha256" (ProtocolScalar21.read) value
            OperationId =
                JsonRead.required "operationId" (JsonRead.nullable (ProtocolScalar10.read)) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownData)
        =
        writer.WriteStartObject()
        JsonWrite.property "artifactKind" (ProtocolScalar46.write) writer value.ArtifactKind
        JsonWrite.property "sourceSha256" (ProtocolScalar21.write) writer value.SourceSha256

        JsonWrite.property
            "operationId"
            (JsonWrite.nullable (ProtocolScalar10.write))
            writer
            value.OperationId

        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknown =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar100.read) value
            Data =
                JsonRead.required
                    "data"
                    (RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownDataJson.read)
                    value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknown)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar100.write) writer value.Tag

        JsonWrite.property
            "data"
            (RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()
