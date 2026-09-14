// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal SemanticDefinitionJson =
    let private properties =
        [
            "contractKind"
            "application"
            "scope"
            "canonicalCommandFormat"
            "requestFingerprintVersion"
            "recoveryEnvelopeFormat"
            "defaultPageSize"
            "maximumPageSize"
            "requestByteLimit"
            "fields"
            "commands"
            "statuses"
            "rules"
        ]

    let read (value: JsonElement) : SemanticDefinition =
        JsonRead.singleton ProtocolDefinition.expected value
        JsonRead.objectValue properties value

        {
            ContractKind = JsonRead.required "contractKind" (ProtocolScalar22.read) value
            Application = JsonRead.required "application" (ProtocolScalar23.read) value
            Scope = JsonRead.required "scope" (ProtocolScalar24.read) value
            CanonicalCommandFormat =
                JsonRead.required "canonicalCommandFormat" (ProtocolScalar25.read) value
            RequestFingerprintVersion =
                JsonRead.required "requestFingerprintVersion" (ProtocolScalar26.read) value
            RecoveryEnvelopeFormat =
                JsonRead.required "recoveryEnvelopeFormat" (ProtocolScalar26.read) value
            DefaultPageSize = JsonRead.required "defaultPageSize" (ProtocolScalar27.read) value
            MaximumPageSize = JsonRead.required "maximumPageSize" (ProtocolScalar27.read) value
            RequestByteLimit = JsonRead.required "requestByteLimit" (ProtocolScalar28.read) value
            Fields =
                JsonRead.required
                    "fields"
                    (JsonRead.array None None (FieldDescriptorJson.read))
                    value
            Commands =
                JsonRead.required
                    "commands"
                    (JsonRead.array None None (CommandDescriptorJson.read))
                    value
            Statuses =
                JsonRead.required "statuses" (JsonRead.array None None (ProtocolScalar6.read)) value
            Rules =
                JsonRead.required
                    "rules"
                    (JsonRead.array None None (SemanticDefinitionRulesItemJson.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: SemanticDefinition) =
        writer.WriteStartObject()
        JsonWrite.property "contractKind" (ProtocolScalar22.write) writer value.ContractKind
        JsonWrite.property "application" (ProtocolScalar23.write) writer value.Application
        JsonWrite.property "scope" (ProtocolScalar24.write) writer value.Scope

        JsonWrite.property
            "canonicalCommandFormat"
            (ProtocolScalar25.write)
            writer
            value.CanonicalCommandFormat

        JsonWrite.property
            "requestFingerprintVersion"
            (ProtocolScalar26.write)
            writer
            value.RequestFingerprintVersion

        JsonWrite.property
            "recoveryEnvelopeFormat"
            (ProtocolScalar26.write)
            writer
            value.RecoveryEnvelopeFormat

        JsonWrite.property "defaultPageSize" (ProtocolScalar27.write) writer value.DefaultPageSize
        JsonWrite.property "maximumPageSize" (ProtocolScalar27.write) writer value.MaximumPageSize
        JsonWrite.property "requestByteLimit" (ProtocolScalar28.write) writer value.RequestByteLimit

        JsonWrite.property
            "fields"
            (JsonWrite.array (FieldDescriptorJson.write))
            writer
            value.Fields

        JsonWrite.property
            "commands"
            (JsonWrite.array (CommandDescriptorJson.write))
            writer
            value.Commands

        JsonWrite.property
            "statuses"
            (JsonWrite.array (ProtocolScalar6.write))
            writer
            value.Statuses

        JsonWrite.property
            "rules"
            (JsonWrite.array (SemanticDefinitionRulesItemJson.write))
            writer
            value.Rules

        writer.WriteEndObject()

module internal DefinitionPayloadJson =
    let private properties =
        [ "semanticFingerprint"; "webFingerprint"; "runtime"; "definition" ]

    let read (value: JsonElement) : DefinitionPayload =
        JsonRead.objectValue properties value

        {
            SemanticFingerprint =
                JsonRead.required "semanticFingerprint" (ProtocolScalar21.read) value
            WebFingerprint = JsonRead.required "webFingerprint" (ProtocolScalar21.read) value
            Runtime = JsonRead.required "runtime" (RuntimeContextJson.read) value
            Definition = JsonRead.required "definition" (SemanticDefinitionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefinitionPayload) =
        writer.WriteStartObject()

        JsonWrite.property
            "semanticFingerprint"
            (ProtocolScalar21.write)
            writer
            value.SemanticFingerprint

        JsonWrite.property "webFingerprint" (ProtocolScalar21.write) writer value.WebFingerprint
        JsonWrite.property "runtime" (RuntimeContextJson.write) writer value.Runtime
        JsonWrite.property "definition" (SemanticDefinitionJson.write) writer value.Definition
        writer.WriteEndObject()

module internal HistoryEntrySummaryJson =
    let private properties = [ "tag"; "change" ]

    let read (value: JsonElement) : HistoryEntrySummary =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar36.read) value
            Change = JsonRead.required "change" (ChangeSummaryJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: HistoryEntrySummary) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar36.write) writer value.Tag
        JsonWrite.property "change" (ChangeSummaryJson.write) writer value.Change
        writer.WriteEndObject()

module internal HistoryEntryFullJson =
    let private properties = [ "tag"; "receipt" ]

    let read (value: JsonElement) : HistoryEntryFull =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar37.read) value
            Receipt = JsonRead.required "receipt" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: HistoryEntryFull) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar37.write) writer value.Tag
        JsonWrite.property "receipt" (ReceiptJson.write) writer value.Receipt
        writer.WriteEndObject()

module internal HistoryEntryJson =
    let read (value: JsonElement) : HistoryEntry =
        match JsonRead.tag "tag" value with
        | "SUMMARY" -> HistoryEntry.Summary((HistoryEntrySummaryJson.read) value)
        | "FULL" -> HistoryEntry.Full((HistoryEntryFullJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: HistoryEntry) =
        match value with
        | HistoryEntry.Summary item -> (HistoryEntrySummaryJson.write) writer item
        | HistoryEntry.Full item -> (HistoryEntryFullJson.write) writer item

module internal HostFailureJson =
    let private properties = [ "kind"; "code"; "message"; "executionPhase" ]

    let read (value: JsonElement) : HostFailure =
        JsonRead.objectValue properties value

        {
            Kind = JsonRead.required "kind" (ProtocolScalar38.read) value
            Code = JsonRead.required "code" (ProtocolScalar8.read) value
            Message = JsonRead.required "message" (ProtocolScalar8.read) value
            ExecutionPhase =
                JsonRead.required "executionPhase" (JsonRead.nullable (ProtocolScalar39.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: HostFailure) =
        writer.WriteStartObject()
        JsonWrite.property "kind" (ProtocolScalar38.write) writer value.Kind
        JsonWrite.property "code" (ProtocolScalar8.write) writer value.Code
        JsonWrite.property "message" (ProtocolScalar8.write) writer value.Message

        JsonWrite.property
            "executionPhase"
            (JsonWrite.nullable (ProtocolScalar39.write))
            writer
            value.ExecutionPhase

        writer.WriteEndObject()

module internal PreparationSummaryJson =
    let private properties =
        [
            "operationId"
            "caseReference"
            "command"
            "preparedAt"
            "state"
            "requestSha256"
            "availableActions"
        ]

    let read (value: JsonElement) : PreparationSummary =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar8.read) value
            Command = JsonRead.required "command" (ProtocolScalar11.read) value
            PreparedAt = JsonRead.required "preparedAt" (ProtocolScalar12.read) value
            State = JsonRead.required "state" (ProtocolScalar40.read) value
            RequestSha256 =
                JsonRead.required "requestSha256" (JsonRead.nullable (ProtocolScalar21.read)) value
            AvailableActions =
                JsonRead.required
                    "availableActions"
                    (JsonRead.array None None (ProtocolScalar41.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: PreparationSummary) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "caseReference" (ProtocolScalar8.write) writer value.CaseReference
        JsonWrite.property "command" (ProtocolScalar11.write) writer value.Command
        JsonWrite.property "preparedAt" (ProtocolScalar12.write) writer value.PreparedAt
        JsonWrite.property "state" (ProtocolScalar40.write) writer value.State

        JsonWrite.property
            "requestSha256"
            (JsonWrite.nullable (ProtocolScalar21.write))
            writer
            value.RequestSha256

        JsonWrite.property
            "availableActions"
            (JsonWrite.array (ProtocolScalar41.write))
            writer
            value.AvailableActions

        writer.WriteEndObject()
