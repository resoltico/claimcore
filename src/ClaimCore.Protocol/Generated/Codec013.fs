// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal RecoveryInspectResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryInspectResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            RecoveryInspectResponseOutcome.Succeeded(
                (RecoveryInspectResponseOutcomeSucceededJson.read) value
            )
        | "REJECTED" ->
            RecoveryInspectResponseOutcome.Rejected(
                (RecoveryListResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            RecoveryInspectResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            RecoveryInspectResponseOutcome.Cancelled(
                (CaseGetResponseOutcomeCancelledJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryInspectResponseOutcome) =
        match value with
        | RecoveryInspectResponseOutcome.Succeeded item ->
            (RecoveryInspectResponseOutcomeSucceededJson.write) writer item
        | RecoveryInspectResponseOutcome.Rejected item ->
            (RecoveryListResponseOutcomeRejectedJson.write) writer item
        | RecoveryInspectResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryInspectResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal RecoveryInspectResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryInspectResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar87.read) value
            Outcome = JsonRead.required "outcome" (RecoveryInspectResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryInspectResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar87.write) writer value.Endpoint
        JsonWrite.property "outcome" (RecoveryInspectResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal RecoveryResolveResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryResolveResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar88.read) value
            Outcome = JsonRead.required "outcome" (CommandExecuteResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryResolveResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar88.write) writer value.Endpoint
        JsonWrite.property "outcome" (CommandExecuteResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeDismissedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeDismissed =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar90.read) value
            Data = JsonRead.required "data" (PreparationDetailsJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeDismissed) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar90.write) writer value.Tag
        JsonWrite.property "data" (PreparationDetailsJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeAlreadyDismissedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeAlreadyDismissed =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar91.read) value
            Data = JsonRead.required "data" (PreparationDetailsJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeAlreadyDismissed) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar91.write) writer value.Tag
        JsonWrite.property "data" (PreparationDetailsJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeNotFoundJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeNotFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar45.read) value
            Data = JsonRead.required "data" (OperationObserveRequestJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeNotFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar45.write) writer value.Tag
        JsonWrite.property "data" (OperationObserveRequestJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeRefusedDataJson =
    let private properties = [ "details"; "rejection" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeRefusedData =
        JsonRead.objectValue properties value

        {
            Details =
                JsonRead.required "details" (JsonRead.nullable (PreparationDetailsJson.read)) value
            Rejection = JsonRead.required "rejection" (RecoveryRejectionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeRefusedData) =
        writer.WriteStartObject()

        JsonWrite.property
            "details"
            (JsonWrite.nullable (PreparationDetailsJson.write))
            writer
            value.Details

        JsonWrite.property "rejection" (RecoveryRejectionJson.write) writer value.Rejection
        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeRefusedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeRefused =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar92.read) value
            Data =
                JsonRead.required "data" (RecoveryDismissResponseOutcomeRefusedDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeRefused) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar92.write) writer value.Tag

        JsonWrite.property
            "data"
            (RecoveryDismissResponseOutcomeRefusedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeDismissStateUnknownJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryDismissResponseOutcomeDismissStateUnknown =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar93.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandPrepareResponseOutcomePreparationStateUnknownDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcomeDismissStateUnknown) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar93.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandPrepareResponseOutcomePreparationStateUnknownDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal RecoveryDismissResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryDismissResponseOutcome =
        match JsonRead.tag "tag" value with
        | "DISMISSED" ->
            RecoveryDismissResponseOutcome.Dismissed(
                (RecoveryDismissResponseOutcomeDismissedJson.read) value
            )
        | "ALREADY_DISMISSED" ->
            RecoveryDismissResponseOutcome.AlreadyDismissed(
                (RecoveryDismissResponseOutcomeAlreadyDismissedJson.read) value
            )
        | "NOT_FOUND" ->
            RecoveryDismissResponseOutcome.NotFound(
                (RecoveryDismissResponseOutcomeNotFoundJson.read) value
            )
        | "REFUSED" ->
            RecoveryDismissResponseOutcome.Refused(
                (RecoveryDismissResponseOutcomeRefusedJson.read) value
            )
        | "FAILED" ->
            RecoveryDismissResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED_BEFORE_ADMISSION" ->
            RecoveryDismissResponseOutcome.CancelledBeforeAdmission(
                (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.read) value
            )
        | "DISMISS_STATE_UNKNOWN" ->
            RecoveryDismissResponseOutcome.DismissStateUnknown(
                (RecoveryDismissResponseOutcomeDismissStateUnknownJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponseOutcome) =
        match value with
        | RecoveryDismissResponseOutcome.Dismissed item ->
            (RecoveryDismissResponseOutcomeDismissedJson.write) writer item
        | RecoveryDismissResponseOutcome.AlreadyDismissed item ->
            (RecoveryDismissResponseOutcomeAlreadyDismissedJson.write) writer item
        | RecoveryDismissResponseOutcome.NotFound item ->
            (RecoveryDismissResponseOutcomeNotFoundJson.write) writer item
        | RecoveryDismissResponseOutcome.Refused item ->
            (RecoveryDismissResponseOutcomeRefusedJson.write) writer item
        | RecoveryDismissResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryDismissResponseOutcome.CancelledBeforeAdmission item ->
            (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.write) writer item
        | RecoveryDismissResponseOutcome.DismissStateUnknown item ->
            (RecoveryDismissResponseOutcomeDismissStateUnknownJson.write) writer item

module internal RecoveryDismissResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryDismissResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar89.read) value
            Outcome = JsonRead.required "outcome" (RecoveryDismissResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar89.write) writer value.Endpoint
        JsonWrite.property "outcome" (RecoveryDismissResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal RecoveryDismissRequestJson =
    let private properties = [ "operationId"; "requestSha256"; "confirmed" ]

    let read (value: JsonElement) : RecoveryDismissRequest =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            RequestSha256 = JsonRead.required "requestSha256" (ProtocolScalar21.read) value
            Confirmed = JsonRead.required "confirmed" (ProtocolScalar94.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDismissRequest) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "requestSha256" (ProtocolScalar21.write) writer value.RequestSha256
        JsonWrite.property "confirmed" (ProtocolScalar94.write) writer value.Confirmed
        writer.WriteEndObject()
