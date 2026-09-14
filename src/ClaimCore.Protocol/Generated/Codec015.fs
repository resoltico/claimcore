// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal RecoveryImportEnvelopeRetainResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryImportEnvelopeRetainResponseOutcome =
        match JsonRead.tag "tag" value with
        | "RETAINED" ->
            RecoveryImportEnvelopeRetainResponseOutcome.Retained(
                (RecoveryImportEnvelopeRetainResponseOutcomeRetainedJson.read) value
            )
        | "EXISTING" ->
            RecoveryImportEnvelopeRetainResponseOutcome.Existing(
                (RecoveryImportEnvelopeRetainResponseOutcomeExistingJson.read) value
            )
        | "REJECTED" ->
            RecoveryImportEnvelopeRetainResponseOutcome.Rejected(
                (RecoveryListResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            RecoveryImportEnvelopeRetainResponseOutcome.Failed(
                (CaseGetResponseOutcomeFailedJson.read) value
            )
        | "CANCELLED_BEFORE_ADMISSION" ->
            RecoveryImportEnvelopeRetainResponseOutcome.CancelledBeforeAdmission(
                (RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmissionJson.read) value
            )
        | "RETAIN_STATE_UNKNOWN" ->
            RecoveryImportEnvelopeRetainResponseOutcome.RetainStateUnknown(
                (RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryImportEnvelopeRetainResponseOutcome) =
        match value with
        | RecoveryImportEnvelopeRetainResponseOutcome.Retained item ->
            (RecoveryImportEnvelopeRetainResponseOutcomeRetainedJson.write) writer item
        | RecoveryImportEnvelopeRetainResponseOutcome.Existing item ->
            (RecoveryImportEnvelopeRetainResponseOutcomeExistingJson.write) writer item
        | RecoveryImportEnvelopeRetainResponseOutcome.Rejected item ->
            (RecoveryListResponseOutcomeRejectedJson.write) writer item
        | RecoveryImportEnvelopeRetainResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryImportEnvelopeRetainResponseOutcome.CancelledBeforeAdmission item ->
            (RecoveryImportEnvelopeRetainResponseOutcomeCancelledBeforeAdmissionJson.write)
                writer
                item
        | RecoveryImportEnvelopeRetainResponseOutcome.RetainStateUnknown item ->
            (RecoveryImportEnvelopeRetainResponseOutcomeRetainStateUnknownJson.write) writer item

module internal RecoveryImportEnvelopeRetainResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryImportEnvelopeRetainResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar97.read) value
            Outcome =
                JsonRead.required
                    "outcome"
                    (RecoveryImportEnvelopeRetainResponseOutcomeJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportEnvelopeRetainResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar97.write) writer value.Endpoint

        JsonWrite.property
            "outcome"
            (RecoveryImportEnvelopeRetainResponseOutcomeJson.write)
            writer
            value.Outcome

        writer.WriteEndObject()

module internal RecoveryImportEnvelopeRetainHeadersJson =
    let private properties = [ "X-ClaimCore-Source-Sha256" ]

    let read (value: JsonElement) : RecoveryImportEnvelopeRetainHeaders =
        JsonRead.objectValue properties value

        {
            XClaimCoreSourceSha256 =
                JsonRead.required "X-ClaimCore-Source-Sha256" (ProtocolScalar21.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportEnvelopeRetainHeaders) =
        writer.WriteStartObject()

        JsonWrite.property
            "X-ClaimCore-Source-Sha256"
            (ProtocolScalar21.write)
            writer
            value.XClaimCoreSourceSha256

        writer.WriteEndObject()

module internal RecoveryImportRecordPreviewResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryImportRecordPreviewResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar101.read) value
            Outcome =
                JsonRead.required
                    "outcome"
                    (RecoveryImportEnvelopePreviewResponseOutcomeJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportRecordPreviewResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar101.write) writer value.Endpoint

        JsonWrite.property
            "outcome"
            (RecoveryImportEnvelopePreviewResponseOutcomeJson.write)
            writer
            value.Outcome

        writer.WriteEndObject()

module internal RecoveryImportRecordRetainResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryImportRecordRetainResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar102.read) value
            Outcome =
                JsonRead.required
                    "outcome"
                    (RecoveryImportEnvelopeRetainResponseOutcomeJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportRecordRetainResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar102.write) writer value.Endpoint

        JsonWrite.property
            "outcome"
            (RecoveryImportEnvelopeRetainResponseOutcomeJson.write)
            writer
            value.Outcome

        writer.WriteEndObject()
