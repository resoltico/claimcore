// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CommandExecuteResponseOutcomeRefusedBeforeAttemptJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeRefusedBeforeAttempt =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar81.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeRefusedBeforeAttemptDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeRefusedBeforeAttempt) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar81.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeRefusedBeforeAttemptDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeFailedBeforeAttemptDataJson =
    let private properties = [ "preparation"; "fault" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeFailedBeforeAttemptData =
        JsonRead.objectValue properties value

        {
            Preparation =
                JsonRead.required
                    "preparation"
                    (JsonRead.nullable (PreparationSummaryJson.read))
                    value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeFailedBeforeAttemptData)
        =
        writer.WriteStartObject()

        JsonWrite.property
            "preparation"
            (JsonWrite.nullable (PreparationSummaryJson.write))
            writer
            value.Preparation

        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeFailedBeforeAttemptJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeFailedBeforeAttempt =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar82.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeFailedBeforeAttemptDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeFailedBeforeAttempt) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar82.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeFailedBeforeAttemptDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeCancelledBeforeAttemptDataJson =
    let private properties = [ "preparation" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeCancelledBeforeAttemptData =
        JsonRead.objectValue properties value

        {
            Preparation = JsonRead.required "preparation" (PreparationSummaryJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeCancelledBeforeAttemptData)
        =
        writer.WriteStartObject()
        JsonWrite.property "preparation" (PreparationSummaryJson.write) writer value.Preparation
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeCancelledBeforeAttemptJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeCancelledBeforeAttempt =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar83.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeCancelledBeforeAttemptDataJson.read)
                    value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeCancelledBeforeAttempt)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar83.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeCancelledBeforeAttemptDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeAttemptAdmissionUnknownDataJson =
    let private properties = [ "preparation"; "fault" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeAttemptAdmissionUnknownData =
        JsonRead.objectValue properties value

        {
            Preparation = JsonRead.required "preparation" (PreparationSummaryJson.read) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeAttemptAdmissionUnknownData)
        =
        writer.WriteStartObject()
        JsonWrite.property "preparation" (PreparationSummaryJson.write) writer value.Preparation
        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeAttemptAdmissionUnknownJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeAttemptAdmissionUnknown =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar84.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeAttemptAdmissionUnknownDataJson.read)
                    value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: CommandExecuteResponseOutcomeAttemptAdmissionUnknown)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar84.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeAttemptAdmissionUnknownDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeAttemptUnresolvedDataJson =
    let private properties = [ "preparation"; "attemptId"; "fault" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeAttemptUnresolvedData =
        JsonRead.objectValue properties value

        {
            Preparation = JsonRead.required "preparation" (PreparationSummaryJson.read) value
            AttemptId = JsonRead.required "attemptId" (ProtocolScalar10.read) value
            Fault = JsonRead.required "fault" (FaultJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeAttemptUnresolvedData) =
        writer.WriteStartObject()
        JsonWrite.property "preparation" (PreparationSummaryJson.write) writer value.Preparation
        JsonWrite.property "attemptId" (ProtocolScalar10.write) writer value.AttemptId
        JsonWrite.property "fault" (FaultJson.write) writer value.Fault
        writer.WriteEndObject()

module internal CommandExecuteResponseOutcomeAttemptUnresolvedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CommandExecuteResponseOutcomeAttemptUnresolved =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar85.read) value
            Data =
                JsonRead.required
                    "data"
                    (CommandExecuteResponseOutcomeAttemptUnresolvedDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: CommandExecuteResponseOutcomeAttemptUnresolved) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar85.write) writer value.Tag

        JsonWrite.property
            "data"
            (CommandExecuteResponseOutcomeAttemptUnresolvedDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()
