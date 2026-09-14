// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandPrepareResponseOutcomePreparedDataJson =
    let private properties = [ "details"; "review" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomePreparedData =
        JsonRead.objectValue properties value

        {
            Details = JsonRead.required "details" (PreparationDetailsJson.read) value
            Review = JsonRead.required "review" (AdvisoryReviewJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomePreparedData) =
        writer.WriteStartObject()
        JsonWrite.property "details" (PreparationDetailsJson.write) writer value.Details
        JsonWrite.property "review" (AdvisoryReviewJson.write) writer value.Review
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomePreparedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomePrepared =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar65.read) value
            Data =
                JsonRead.required "data" (CommandPrepareResponseOutcomePreparedDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomePrepared) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar65.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomePreparedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeObservedAcceptedDataJson =
    let private properties = [ "details"; "receipt" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeObservedAcceptedData =
        JsonRead.objectValue properties value

        {
            Details = JsonRead.required "details" (PreparationDetailsJson.read) value
            Receipt = JsonRead.required "receipt" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeObservedAcceptedData) =
        writer.WriteStartObject()
        JsonWrite.property "details" (PreparationDetailsJson.write) writer value.Details
        JsonWrite.property "receipt" (ReceiptJson.write) writer value.Receipt
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeObservedAcceptedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeObservedAccepted =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar66.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandPrepareResponseOutcomeObservedAcceptedDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeObservedAccepted) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar66.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomeObservedAcceptedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeRetainedForRecoveryDataJson =
    let private properties = [ "details"; "rejection" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeRetainedForRecoveryData =
        JsonRead.objectValue properties value

        {
            Details = JsonRead.required "details" (PreparationDetailsJson.read) value
            Rejection = JsonRead.required "rejection" (RejectionJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandPrepareResponseOutcomeRetainedForRecoveryData)
        =
        writer.WriteStartObject()
        JsonWrite.property "details" (PreparationDetailsJson.write) writer value.Details
        JsonWrite.property "rejection" (RejectionJson.write) writer value.Rejection
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeRetainedForRecoveryJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeRetainedForRecovery =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar67.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandPrepareResponseOutcomeRetainedForRecoveryDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeRetainedForRecovery) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar67.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomeRetainedForRecoveryDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeRejectedDataJson =
    let private properties = [ "operationId"; "rejection" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeRejectedData =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Rejection = JsonRead.required "rejection" (RejectionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeRejectedData) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "rejection" (RejectionJson.write) writer value.Rejection
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeRejectedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeRejected =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar16.read) value
            Data =
                JsonRead.required "data" (CommandPrepareResponseOutcomeRejectedDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeRejected) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar16.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomeRejectedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeFailedDataJson =
    let private properties = [ "operationId"; "fault" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeFailedData =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeFailedData) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeFailedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeFailed =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar56.read) value
            Data = JsonRead.required "data" (CommandPrepareResponseOutcomeFailedDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareResponseOutcomeFailed) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar56.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomeFailedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomeCancelledBeforeAdmission =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar68.read) value
            Data = JsonRead.required "data" (OperationObserveRequestJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandPrepareResponseOutcomeCancelledBeforeAdmission)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar68.write) writer value.Tag
        JsonWrite.property "data" (OperationObserveRequestJson.write) writer value.Data
        writer.WriteEndObject()

module internal CommandPrepareResponseOutcomePreparationStateUnknownDataJson =
    let private properties = [ "operationId"; "requestSha256"; "fault" ]

    let read (value: JsonElement) : CommandPrepareResponseOutcomePreparationStateUnknownData =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            RequestSha256 = JsonRead.required "requestSha256" (ProtocolScalar21.read) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandPrepareResponseOutcomePreparationStateUnknownData)
        =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "requestSha256" (ProtocolScalar21.write) writer value.RequestSha256
        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()
