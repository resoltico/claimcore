// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandExecuteResponseOutcomeJson =
    let read (value: JsonElement) : CommandExecuteResponseOutcome =
        match JsonRead.tag "tag" value with
        | "OBSERVED_ACCEPTED" ->
            CommandExecuteResponseOutcome.ObservedAccepted(
                (CommandExecuteResponseOutcomeObservedAcceptedJson.read) value
            )
        | "COMPLETED" ->
            CommandExecuteResponseOutcome.Completed(
                (CommandExecuteResponseOutcomeCompletedJson.read) value
            )
        | "REFUSED_BEFORE_ATTEMPT" ->
            CommandExecuteResponseOutcome.RefusedBeforeAttempt(
                (CommandExecuteResponseOutcomeRefusedBeforeAttemptJson.read) value
            )
        | "FAILED_BEFORE_ATTEMPT" ->
            CommandExecuteResponseOutcome.FailedBeforeAttempt(
                (CommandExecuteResponseOutcomeFailedBeforeAttemptJson.read) value
            )
        | "CANCELLED_BEFORE_ADMISSION" ->
            CommandExecuteResponseOutcome.CancelledBeforeAdmission(
                (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.read) value
            )
        | "CANCELLED_BEFORE_ATTEMPT" ->
            CommandExecuteResponseOutcome.CancelledBeforeAttempt(
                (CommandExecuteResponseOutcomeCancelledBeforeAttemptJson.read) value
            )
        | "ATTEMPT_ADMISSION_UNKNOWN" ->
            CommandExecuteResponseOutcome.AttemptAdmissionUnknown(
                (CommandExecuteResponseOutcomeAttemptAdmissionUnknownJson.read) value
            )
        | "ATTEMPT_UNRESOLVED" ->
            CommandExecuteResponseOutcome.AttemptUnresolved(
                (CommandExecuteResponseOutcomeAttemptUnresolvedJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcome) =
        match value with
        | CommandExecuteResponseOutcome.ObservedAccepted item ->
            (CommandExecuteResponseOutcomeObservedAcceptedJson.write) writer item
        | CommandExecuteResponseOutcome.Completed item ->
            (CommandExecuteResponseOutcomeCompletedJson.write) writer item
        | CommandExecuteResponseOutcome.RefusedBeforeAttempt item ->
            (CommandExecuteResponseOutcomeRefusedBeforeAttemptJson.write) writer item
        | CommandExecuteResponseOutcome.FailedBeforeAttempt item ->
            (CommandExecuteResponseOutcomeFailedBeforeAttemptJson.write) writer item
        | CommandExecuteResponseOutcome.CancelledBeforeAdmission item ->
            (CommandPrepareResponseOutcomeCancelledBeforeAdmissionJson.write) writer item
        | CommandExecuteResponseOutcome.CancelledBeforeAttempt item ->
            (CommandExecuteResponseOutcomeCancelledBeforeAttemptJson.write) writer item
        | CommandExecuteResponseOutcome.AttemptAdmissionUnknown item ->
            (CommandExecuteResponseOutcomeAttemptAdmissionUnknownJson.write) writer item
        | CommandExecuteResponseOutcome.AttemptUnresolved item ->
            (CommandExecuteResponseOutcomeAttemptUnresolvedJson.write) writer item

module internal CommandExecuteResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : CommandExecuteResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar78.read) value
            Outcome = JsonRead.required "outcome" (CommandExecuteResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar78.write) writer value.Endpoint
        JsonWrite.property "outcome" (CommandExecuteResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CommandExecuteRequestJson =
    let private properties = [ "operationId"; "requestSha256" ]

    let read (value: JsonElement) : CommandExecuteRequest =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            RequestSha256 = JsonRead.required "requestSha256" (ProtocolScalar21.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteRequest) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "requestSha256" (ProtocolScalar21.write) writer value.RequestSha256
        writer.WriteEndObject()

module internal RecoveryListResponseOutcomeSucceededDataJson =
    let private properties = [ "items"; "nextCursor" ]

    let read (value: JsonElement) : RecoveryListResponseOutcomeSucceededData =
        JsonRead.objectValue properties value

        {
            Items =
                JsonRead.required
                    "items"
                    (JsonRead.array None None (PreparationSummaryJson.read))
                    value
            NextCursor =
                JsonRead.required "nextCursor" (JsonRead.nullable (ProtocolScalar8.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryListResponseOutcomeSucceededData) =
        writer.WriteStartObject()

        JsonWrite.property
            "items"
            (JsonWrite.array (PreparationSummaryJson.write))
            writer
            value.Items

        JsonWrite.property
            "nextCursor"
            (JsonWrite.nullable (ProtocolScalar8.write))
            writer
            value.NextCursor

        writer.WriteEndObject()

module internal RecoveryListResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryListResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data =
                JsonRead.required "data" (RecoveryListResponseOutcomeSucceededDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryListResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag

        JsonWrite.property
            "data"
            (RecoveryListResponseOutcomeSucceededDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal RecoveryListResponseOutcomeRejectedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryListResponseOutcomeRejected =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar16.read) value
            Data = JsonRead.required "data" (RecoveryRejectionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryListResponseOutcomeRejected) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar16.write) writer value.Tag
        JsonWrite.property "data" (RecoveryRejectionJson.write) writer value.Data
        writer.WriteEndObject()

module internal RecoveryListResponseOutcomeJson =
    let read (value: JsonElement) : RecoveryListResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            RecoveryListResponseOutcome.Succeeded(
                (RecoveryListResponseOutcomeSucceededJson.read) value
            )
        | "REJECTED" ->
            RecoveryListResponseOutcome.Rejected(
                (RecoveryListResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            RecoveryListResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            RecoveryListResponseOutcome.Cancelled((CaseGetResponseOutcomeCancelledJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryListResponseOutcome) =
        match value with
        | RecoveryListResponseOutcome.Succeeded item ->
            (RecoveryListResponseOutcomeSucceededJson.write) writer item
        | RecoveryListResponseOutcome.Rejected item ->
            (RecoveryListResponseOutcomeRejectedJson.write) writer item
        | RecoveryListResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | RecoveryListResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal RecoveryListResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : RecoveryListResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar86.read) value
            Outcome = JsonRead.required "outcome" (RecoveryListResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryListResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar86.write) writer value.Endpoint
        JsonWrite.property "outcome" (RecoveryListResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal RecoveryInspectResponseOutcomeSucceededDataFoundJson =
    let private properties = [ "tag"; "value" ]

    let read (value: JsonElement) : RecoveryInspectResponseOutcomeSucceededDataFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar44.read) value
            Value = JsonRead.required "value" (RecoveryDetailsJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryInspectResponseOutcomeSucceededDataFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar44.write) writer value.Tag
        JsonWrite.property "value" (RecoveryDetailsJson.write) writer value.Value
        writer.WriteEndObject()

module internal RecoveryInspectResponseOutcomeSucceededDataJson =
    let read (value: JsonElement) : RecoveryInspectResponseOutcomeSucceededData =
        match JsonRead.tag "tag" value with
        | "FOUND" ->
            RecoveryInspectResponseOutcomeSucceededData.Found(
                (RecoveryInspectResponseOutcomeSucceededDataFoundJson.read) value
            )
        | "NOT_FOUND" ->
            RecoveryInspectResponseOutcomeSucceededData.NotFound(
                (RecoveryDetailsObservationNotFoundJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryInspectResponseOutcomeSucceededData) =
        match value with
        | RecoveryInspectResponseOutcomeSucceededData.Found item ->
            (RecoveryInspectResponseOutcomeSucceededDataFoundJson.write) writer item
        | RecoveryInspectResponseOutcomeSucceededData.NotFound item ->
            (RecoveryDetailsObservationNotFoundJson.write) writer item

module internal RecoveryInspectResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : RecoveryInspectResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data =
                JsonRead.required
                    "data"
                    (RecoveryInspectResponseOutcomeSucceededDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryInspectResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag

        JsonWrite.property
            "data"
            (RecoveryInspectResponseOutcomeSucceededDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()
