// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal PreparationDetailsAuthoredValuesItemJson =
    let private properties = [ "name"; "value" ]

    let read (value: JsonElement) : PreparationDetailsAuthoredValuesItem =
        JsonRead.objectValue properties value

        {
            Name = JsonRead.required "name" (ProtocolScalar8.read) value
            Value = JsonRead.required "value" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: PreparationDetailsAuthoredValuesItem) =
        writer.WriteStartObject()
        JsonWrite.property "name" (ProtocolScalar8.write) writer value.Name
        JsonWrite.property "value" (ProtocolScalar8.write) writer value.Value
        writer.WriteEndObject()

module internal PreparationDetailsAttemptsItemJson =
    let private properties = [ "attemptId"; "startedAt"; "settlement"; "settledAt" ]

    let read (value: JsonElement) : PreparationDetailsAttemptsItem =
        JsonRead.objectValue properties value

        {
            AttemptId = JsonRead.required "attemptId" (ProtocolScalar10.read) value
            StartedAt = JsonRead.required "startedAt" (ProtocolScalar12.read) value
            Settlement =
                JsonRead.required "settlement" (JsonRead.nullable (ProtocolScalar43.read)) value
            SettledAt =
                JsonRead.required "settledAt" (JsonRead.nullable (ProtocolScalar12.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: PreparationDetailsAttemptsItem) =
        writer.WriteStartObject()
        JsonWrite.property "attemptId" (ProtocolScalar10.write) writer value.AttemptId
        JsonWrite.property "startedAt" (ProtocolScalar12.write) writer value.StartedAt

        JsonWrite.property
            "settlement"
            (JsonWrite.nullable (ProtocolScalar43.write))
            writer
            value.Settlement

        JsonWrite.property
            "settledAt"
            (JsonWrite.nullable (ProtocolScalar12.write))
            writer
            value.SettledAt

        writer.WriteEndObject()

module internal PreparationDetailsJson =
    let private properties =
        [
            "summary"
            "expectedRevision"
            "authoredValues"
            "canonicalCommandFormat"
            "preparingApplicationVersion"
            "preparingContractFingerprint"
            "preparingContractKind"
            "attempts"
            "legacyUncertainty"
        ]

    let read (value: JsonElement) : PreparationDetails =
        JsonRead.objectValue properties value

        {
            Summary = JsonRead.required "summary" (PreparationSummaryJson.read) value
            ExpectedRevision = JsonRead.required "expectedRevision" (ProtocolScalar7.read) value
            AuthoredValues =
                JsonRead.required
                    "authoredValues"
                    (JsonRead.array None None (PreparationDetailsAuthoredValuesItemJson.read))
                    value
            CanonicalCommandFormat =
                JsonRead.required "canonicalCommandFormat" (ProtocolScalar25.read) value
            PreparingApplicationVersion =
                JsonRead.required "preparingApplicationVersion" (ProtocolScalar8.read) value
            PreparingContractFingerprint =
                JsonRead.required "preparingContractFingerprint" (ProtocolScalar21.read) value
            PreparingContractKind =
                JsonRead.required "preparingContractKind" (ProtocolScalar42.read) value
            Attempts =
                JsonRead.required
                    "attempts"
                    (JsonRead.array None None (PreparationDetailsAttemptsItemJson.read))
                    value
            LegacyUncertainty = JsonRead.required "legacyUncertainty" (ProtocolScalar9.read) value
        }

    let write (writer: Utf8JsonWriter) (value: PreparationDetails) =
        writer.WriteStartObject()
        JsonWrite.property "summary" (PreparationSummaryJson.write) writer value.Summary
        JsonWrite.property "expectedRevision" (ProtocolScalar7.write) writer value.ExpectedRevision

        JsonWrite.property
            "authoredValues"
            (JsonWrite.array (PreparationDetailsAuthoredValuesItemJson.write))
            writer
            value.AuthoredValues

        JsonWrite.property
            "canonicalCommandFormat"
            (ProtocolScalar25.write)
            writer
            value.CanonicalCommandFormat

        JsonWrite.property
            "preparingApplicationVersion"
            (ProtocolScalar8.write)
            writer
            value.PreparingApplicationVersion

        JsonWrite.property
            "preparingContractFingerprint"
            (ProtocolScalar21.write)
            writer
            value.PreparingContractFingerprint

        JsonWrite.property
            "preparingContractKind"
            (ProtocolScalar42.write)
            writer
            value.PreparingContractKind

        JsonWrite.property
            "attempts"
            (JsonWrite.array (PreparationDetailsAttemptsItemJson.write))
            writer
            value.Attempts

        JsonWrite.property
            "legacyUncertainty"
            (ProtocolScalar9.write)
            writer
            value.LegacyUncertainty

        writer.WriteEndObject()

module internal RecoveryDetailsObservationFoundJson =
    let private properties = [ "tag"; "value" ]

    let read (value: JsonElement) : RecoveryDetailsObservationFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar44.read) value
            Value = JsonRead.required "value" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDetailsObservationFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar44.write) writer value.Tag
        JsonWrite.property "value" (ReceiptJson.write) writer value.Value
        writer.WriteEndObject()

module internal RecoveryDetailsObservationNotFoundJson =
    let private properties = [ "tag"; "identity" ]

    let read (value: JsonElement) : RecoveryDetailsObservationNotFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar45.read) value
            Identity = JsonRead.required "identity" (ProtocolScalar10.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDetailsObservationNotFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar45.write) writer value.Tag
        JsonWrite.property "identity" (ProtocolScalar10.write) writer value.Identity
        writer.WriteEndObject()

module internal RecoveryDetailsObservationJson =
    let read (value: JsonElement) : RecoveryDetailsObservation =
        match JsonRead.tag "tag" value with
        | "FOUND" ->
            RecoveryDetailsObservation.Found((RecoveryDetailsObservationFoundJson.read) value)
        | "NOT_FOUND" ->
            RecoveryDetailsObservation.NotFound((RecoveryDetailsObservationNotFoundJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: RecoveryDetailsObservation) =
        match value with
        | RecoveryDetailsObservation.Found item ->
            (RecoveryDetailsObservationFoundJson.write) writer item
        | RecoveryDetailsObservation.NotFound item ->
            (RecoveryDetailsObservationNotFoundJson.write) writer item

module internal RecoveryDetailsJson =
    let private properties = [ "preparation"; "observation" ]

    let read (value: JsonElement) : RecoveryDetails =
        JsonRead.objectValue properties value

        {
            Preparation = JsonRead.required "preparation" (PreparationDetailsJson.read) value
            Observation =
                JsonRead.required "observation" (RecoveryDetailsObservationJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryDetails) =
        writer.WriteStartObject()
        JsonWrite.property "preparation" (PreparationDetailsJson.write) writer value.Preparation

        JsonWrite.property
            "observation"
            (RecoveryDetailsObservationJson.write)
            writer
            value.Observation

        writer.WriteEndObject()

module internal RecoveryImportPreviewDecodedEffectJson =
    let private properties =
        [
            "operationId"
            "caseReference"
            "command"
            "expectedRevision"
            "authoredValues"
            "canonicalCommandFormat"
            "requestSha256"
        ]

    let read (value: JsonElement) : RecoveryImportPreviewDecodedEffect =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar8.read) value
            Command = JsonRead.required "command" (ProtocolScalar11.read) value
            ExpectedRevision = JsonRead.required "expectedRevision" (ProtocolScalar7.read) value
            AuthoredValues =
                JsonRead.required
                    "authoredValues"
                    (JsonRead.array None None (PreparationDetailsAuthoredValuesItemJson.read))
                    value
            CanonicalCommandFormat =
                JsonRead.required "canonicalCommandFormat" (ProtocolScalar25.read) value
            RequestSha256 = JsonRead.required "requestSha256" (ProtocolScalar21.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportPreviewDecodedEffect) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        JsonWrite.property "caseReference" (ProtocolScalar8.write) writer value.CaseReference
        JsonWrite.property "command" (ProtocolScalar11.write) writer value.Command
        JsonWrite.property "expectedRevision" (ProtocolScalar7.write) writer value.ExpectedRevision

        JsonWrite.property
            "authoredValues"
            (JsonWrite.array (PreparationDetailsAuthoredValuesItemJson.write))
            writer
            value.AuthoredValues

        JsonWrite.property
            "canonicalCommandFormat"
            (ProtocolScalar25.write)
            writer
            value.CanonicalCommandFormat

        JsonWrite.property "requestSha256" (ProtocolScalar21.write) writer value.RequestSha256
        writer.WriteEndObject()
