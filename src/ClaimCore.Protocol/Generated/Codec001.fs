// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandDescriptorJson =
    let private properties = [ "kind"; "label"; "meaning"; "inputs" ]

    let read (value: JsonElement) : CommandDescriptor =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar11.read) value
            Label = JsonRead.required "label" (ProtocolScalar8.read) value
            Meaning = JsonRead.required "meaning" (ProtocolScalar8.read) value
            Inputs =
                JsonRead.required
                    "inputs"
                    (JsonRead.array None None (CommandInputDescriptorJson.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandDescriptor) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar11.write) writer value.Kind
        JsonWrite.property "label" (ProtocolScalar8.write) writer value.Label
        JsonWrite.property "meaning" (ProtocolScalar8.write) writer value.Meaning

        JsonWrite.property
            "inputs"
            (JsonWrite.array (CommandInputDescriptorJson.write))
            writer
            value.Inputs

        writer.WriteEndObject()

module internal CurrentCaseJson =
    let private properties = [ "case"; "availableCommands" ]

    let read (value: JsonElement) : CurrentCase =
        JsonRead.objectValue properties value

        {
            Case = JsonRead.required "case" (CaseViewJson.read) value
            AvailableCommands =
                JsonRead.required
                    "availableCommands"
                    (JsonRead.array None None (ProtocolScalar11.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CurrentCase) =
        writer.WriteStartObject()
        JsonWrite.property "case" (CaseViewJson.write) writer value.Case

        JsonWrite.property
            "availableCommands"
            (JsonWrite.array (ProtocolScalar11.write))
            writer
            value.AvailableCommands

        writer.WriteEndObject()

module internal ReceiptJson =
    let private properties =
        [ "operationId"; "snapshot"; "recordedAt"; "recordedBy"; "replayed"; "command" ]

    let read (value: JsonElement) : Receipt =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Snapshot = JsonRead.required "snapshot" (CaseViewJson.read) value
            RecordedAt = JsonRead.required "recordedAt" (ProtocolScalar12.read) value
            RecordedBy = JsonRead.required "recordedBy" (ProtocolScalar8.read) value
            Replayed = JsonRead.required "replayed" (ProtocolScalar9.read) value
            Command = JsonRead.required "command" (ProtocolScalar11.read) value
        }

    let write (writer: Utf8JsonWriter) (value: Receipt) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "snapshot" (CaseViewJson.write) writer value.Snapshot
        JsonWrite.property "recordedAt" (ProtocolScalar12.write) writer value.RecordedAt
        JsonWrite.property "recordedBy" (ProtocolScalar8.write) writer value.RecordedBy
        JsonWrite.property "replayed" (ProtocolScalar9.write) writer value.Replayed
        JsonWrite.property "command" (ProtocolScalar11.write) writer value.Command
        writer.WriteEndObject()

module internal DefiniteExecutionAcceptedJson =
    let private properties = [ "tag"; "receipt" ]

    let read (value: JsonElement) : DefiniteExecutionAccepted =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar15.read) value
            Receipt = JsonRead.required "receipt" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefiniteExecutionAccepted) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar15.write) writer value.Tag
        JsonWrite.property "receipt" (ReceiptJson.write) writer value.Receipt
        writer.WriteEndObject()

module internal RejectionJson =
    let private properties =
        [ "code"; "message"; "field"; "actualRevision"; "recommendedAction" ]

    let read (value: JsonElement) : Rejection =
        JsonRead.objectValue properties value

        {
            Code = JsonRead.required "code" (ProtocolScalar17.read) value
            Message = JsonRead.required "message" (ProtocolScalar8.read) value
            Field = JsonRead.required "field" (JsonRead.nullable (ProtocolScalar8.read)) value
            ActualRevision =
                JsonRead.required "actualRevision" (JsonRead.nullable (ProtocolScalar7.read)) value
            RecommendedAction = JsonRead.required "recommendedAction" (ProtocolScalar18.read) value
        }

    let write (writer: Utf8JsonWriter) (value: Rejection) =
        writer.WriteStartObject()
        JsonWrite.property "code" (ProtocolScalar17.write) writer value.Code
        JsonWrite.property "message" (ProtocolScalar8.write) writer value.Message
        JsonWrite.property "field" (JsonWrite.nullable (ProtocolScalar8.write)) writer value.Field

        JsonWrite.property
            "actualRevision"
            (JsonWrite.nullable (ProtocolScalar7.write))
            writer
            value.ActualRevision

        JsonWrite.property
            "recommendedAction"
            (ProtocolScalar18.write)
            writer
            value.RecommendedAction

        writer.WriteEndObject()

module internal DefiniteExecutionRejectedJson =
    let private properties = [ "tag"; "operationId"; "rejection" ]

    let read (value: JsonElement) : DefiniteExecutionRejected =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar16.read) value
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Rejection = JsonRead.required "rejection" (RejectionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefiniteExecutionRejected) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar16.write) writer value.Tag
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "rejection" (RejectionJson.write) writer value.Rejection
        writer.WriteEndObject()

module internal FaultJson =
    let private properties = [ "code"; "message"; "recommendedAction" ]

    let read (value: JsonElement) : Fault =
        JsonRead.objectValue properties value

        {
            Code = JsonRead.required "code" (ProtocolScalar20.read) value
            Message = JsonRead.required "message" (ProtocolScalar8.read) value
            RecommendedAction = JsonRead.required "recommendedAction" (ProtocolScalar18.read) value
        }

    let write (writer: Utf8JsonWriter) (value: Fault) =
        writer.WriteStartObject()
        JsonWrite.property "code" (ProtocolScalar20.write) writer value.Code
        JsonWrite.property "message" (ProtocolScalar8.write) writer value.Message

        JsonWrite.property
            "recommendedAction"
            (ProtocolScalar18.write)
            writer
            value.RecommendedAction

        writer.WriteEndObject()

module internal DefiniteExecutionFailedBeforeCommitJson =
    let private properties = [ "tag"; "operationId"; "fault" ]

    let read (value: JsonElement) : DefiniteExecutionFailedBeforeCommit =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar19.read) value
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefiniteExecutionFailedBeforeCommit) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar19.write) writer value.Tag
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal DefiniteExecutionJson =
    let read (value: JsonElement) : DefiniteExecution =
        match JsonRead.tag "tag" value with
        | "ACCEPTED" -> DefiniteExecution.Accepted((DefiniteExecutionAcceptedJson.read) value)
        | "REJECTED" -> DefiniteExecution.Rejected((DefiniteExecutionRejectedJson.read) value)
        | "FAILED_BEFORE_COMMIT" ->
            DefiniteExecution.FailedBeforeCommit(
                (DefiniteExecutionFailedBeforeCommitJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: DefiniteExecution) =
        match value with
        | DefiniteExecution.Accepted item -> (DefiniteExecutionAcceptedJson.write) writer item
        | DefiniteExecution.Rejected item -> (DefiniteExecutionRejectedJson.write) writer item
        | DefiniteExecution.FailedBeforeCommit item ->
            (DefiniteExecutionFailedBeforeCommitJson.write) writer item
