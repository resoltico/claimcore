// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandPrepareRequestCommandRecordPaymentJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandRecordPayment =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar74.read) value
            Values =
                JsonRead.required
                    "values"
                    (CommandPrepareRequestCommandRecordPaymentValuesJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandRecordPayment) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar74.write) writer value.Kind

        JsonWrite.property
            "values"
            (CommandPrepareRequestCommandRecordPaymentValuesJson.write)
            writer
            value.Values

        writer.WriteEndObject()

module internal CommandPrepareRequestCommandClearPaymentJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandClearPayment =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar75.read) value
            Values = JsonRead.required "values" (SessionLogoutRequestJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandClearPayment) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar75.write) writer value.Kind
        JsonWrite.property "values" (SessionLogoutRequestJson.write) writer value.Values
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandCloseJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandClose =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar76.read) value
            Values = JsonRead.required "values" (SessionLogoutRequestJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandClose) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar76.write) writer value.Kind
        JsonWrite.property "values" (SessionLogoutRequestJson.write) writer value.Values
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandReopenJson =
    let private properties = [ "kind"; "values" ]

    let read (value: JsonElement) : CommandPrepareRequestCommandReopen =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar77.read) value
            Values = JsonRead.required "values" (SessionLogoutRequestJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommandReopen) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar77.write) writer value.Kind
        JsonWrite.property "values" (SessionLogoutRequestJson.write) writer value.Values
        writer.WriteEndObject()

module internal CommandPrepareRequestCommandJson =
    let read (value: JsonElement) : CommandPrepareRequestCommand =
        match JsonRead.tag "kind" value with
        | "OPEN" ->
            CommandPrepareRequestCommand.Open((CommandPrepareRequestCommandOpenJson.read) value)
        | "AMEND_REGISTRATION" ->
            CommandPrepareRequestCommand.AmendRegistration(
                (CommandPrepareRequestCommandAmendRegistrationJson.read) value
            )
        | "DECIDE" ->
            CommandPrepareRequestCommand.Decide((CommandPrepareRequestCommandDecideJson.read) value)
        | "WITHDRAW_DECISION" ->
            CommandPrepareRequestCommand.WithdrawDecision(
                (CommandPrepareRequestCommandWithdrawDecisionJson.read) value
            )
        | "RECORD_PAYMENT" ->
            CommandPrepareRequestCommand.RecordPayment(
                (CommandPrepareRequestCommandRecordPaymentJson.read) value
            )
        | "CLEAR_PAYMENT" ->
            CommandPrepareRequestCommand.ClearPayment(
                (CommandPrepareRequestCommandClearPaymentJson.read) value
            )
        | "CLOSE" ->
            CommandPrepareRequestCommand.Close((CommandPrepareRequestCommandCloseJson.read) value)
        | "REOPEN" ->
            CommandPrepareRequestCommand.Reopen((CommandPrepareRequestCommandReopenJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequestCommand) =
        match value with
        | CommandPrepareRequestCommand.Open item ->
            (CommandPrepareRequestCommandOpenJson.write) writer item
        | CommandPrepareRequestCommand.AmendRegistration item ->
            (CommandPrepareRequestCommandAmendRegistrationJson.write) writer item
        | CommandPrepareRequestCommand.Decide item ->
            (CommandPrepareRequestCommandDecideJson.write) writer item
        | CommandPrepareRequestCommand.WithdrawDecision item ->
            (CommandPrepareRequestCommandWithdrawDecisionJson.write) writer item
        | CommandPrepareRequestCommand.RecordPayment item ->
            (CommandPrepareRequestCommandRecordPaymentJson.write) writer item
        | CommandPrepareRequestCommand.ClearPayment item ->
            (CommandPrepareRequestCommandClearPaymentJson.write) writer item
        | CommandPrepareRequestCommand.Close item ->
            (CommandPrepareRequestCommandCloseJson.write) writer item
        | CommandPrepareRequestCommand.Reopen item ->
            (CommandPrepareRequestCommandReopenJson.write) writer item

module internal CommandPrepareRequestJson =
    let private properties =
        [ "operationId"; "caseReference"; "expectedRevision"; "command" ]

    let read (value: JsonElement) : CommandPrepareRequest =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar5.read) value
            ExpectedRevision = JsonRead.required "expectedRevision" (ProtocolScalar7.read) value
            Command = JsonRead.required "command" (CommandPrepareRequestCommandJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandPrepareRequest) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "caseReference" (ProtocolScalar5.write) writer value.CaseReference
        JsonWrite.property "expectedRevision" (ProtocolScalar7.write) writer value.ExpectedRevision
        JsonWrite.property "command" (CommandPrepareRequestCommandJson.write) writer value.Command
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeObservedAcceptedDataJson =
    let private properties = [ "receipt" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeObservedAcceptedData =
        JsonRead.objectValue properties value

        {
            Receipt = JsonRead.required "receipt" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeObservedAcceptedData) =
        writer.WriteStartObject()
        JsonWrite.property "receipt" (ReceiptJson.write) writer value.Receipt
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeObservedAcceptedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeObservedAccepted =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar66.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeObservedAcceptedDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeObservedAccepted) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar66.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeObservedAcceptedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeCompletedDataJson =
    let private properties = [ "preparation"; "attemptId"; "execution"; "settlement" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeCompletedData =
        JsonRead.objectValue properties value

        {
            Preparation = JsonRead.required "preparation" (PreparationSummaryJson.read) value
            AttemptId = JsonRead.required "attemptId" (ProtocolScalar10.read) value
            Execution = JsonRead.required "execution" (DefiniteExecutionJson.read) value
            Settlement = JsonRead.required "settlement" (ProtocolScalar80.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeCompletedData) =
        writer.WriteStartObject()
        JsonWrite.property "preparation" (PreparationSummaryJson.write) writer value.Preparation
        JsonWrite.property "attemptId" (ProtocolScalar10.write) writer value.AttemptId
        JsonWrite.property "execution" (DefiniteExecutionJson.write) writer value.Execution
        JsonWrite.property "settlement" (ProtocolScalar80.write) writer value.Settlement
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeCompletedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeCompleted =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar79.read) value
            Data =
                JsonRead.required "data" (CommandExecuteResponseOutcomeCompletedDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeCompleted) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar79.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeCompletedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeRefusedBeforeAttemptDataJson =
    let private properties = [ "preparation"; "rejection" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeRefusedBeforeAttemptData =
        JsonRead.objectValue properties value

        {
            Preparation =
                JsonRead.required
                    "preparation"
                    (JsonRead.nullable (PreparationSummaryJson.read))
                    value
            Rejection = JsonRead.required "rejection" (RecoveryRejectionJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeRefusedBeforeAttemptData)
        =
        writer.WriteStartObject()

        JsonWrite.property
            "preparation"
            (JsonWrite.nullable (PreparationSummaryJson.write))
            writer
            value.Preparation

        JsonWrite.property "rejection" (RecoveryRejectionJson.write) writer value.Rejection
        writer.WriteEndObject()
