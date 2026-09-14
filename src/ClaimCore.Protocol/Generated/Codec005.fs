// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal RecoveryImportPreviewJson =
    let private properties =
        [ "artifactKind"; "sourceSha256"; "decodedEffect"; "existingPreparation" ]

    let read (value: JsonElement) : RecoveryImportPreview =
        JsonRead.objectValue properties value

        {
            ArtifactKind = JsonRead.required "artifactKind" (ProtocolScalar46.read) value
            SourceSha256 = JsonRead.required "sourceSha256" (ProtocolScalar21.read) value
            DecodedEffect =
                JsonRead.required
                    "decodedEffect"
                    (RecoveryImportPreviewDecodedEffectJson.read)
                    value
            ExistingPreparation =
                JsonRead.required
                    "existingPreparation"
                    (JsonRead.nullable (PreparationSummaryJson.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryImportPreview) =
        writer.WriteStartObject()
        JsonWrite.property "artifactKind" (ProtocolScalar46.write) writer value.ArtifactKind
        JsonWrite.property "sourceSha256" (ProtocolScalar21.write) writer value.SourceSha256

        JsonWrite.property
            "decodedEffect"
            (RecoveryImportPreviewDecodedEffectJson.write)
            writer
            value.DecodedEffect

        JsonWrite.property
            "existingPreparation"
            (JsonWrite.nullable (PreparationSummaryJson.write))
            writer
            value.ExistingPreparation

        writer.WriteEndObject()

module internal RecoveryRejectionJson =
    let private properties = [ "code"; "message"; "recommendedAction" ]

    let read (value: JsonElement) : RecoveryRejection =
        JsonRead.objectValue properties value

        {
            Code = JsonRead.required "code" (ProtocolScalar47.read) value
            Message = JsonRead.required "message" (ProtocolScalar8.read) value
            RecommendedAction = JsonRead.required "recommendedAction" (ProtocolScalar18.read) value
        }

    let write (writer: Utf8JsonWriter) (value: RecoveryRejection) =
        writer.WriteStartObject()
        JsonWrite.property "code" (ProtocolScalar47.write) writer value.Code
        JsonWrite.property "message" (ProtocolScalar8.write) writer value.Message

        JsonWrite.property
            "recommendedAction"
            (ProtocolScalar18.write)
            writer
            value.RecommendedAction

        writer.WriteEndObject()

module internal SessionSnapshotJson =
    let private properties = [ "authenticated"; "antiforgeryToken" ]

    let read (value: JsonElement) : SessionSnapshot =
        JsonRead.objectValue properties value

        {
            Authenticated = JsonRead.required "authenticated" (ProtocolScalar9.read) value
            AntiforgeryToken =
                JsonRead.required
                    "antiforgeryToken"
                    (JsonRead.nullable (ProtocolScalar8.read))
                    value
        }

    let write (writer: Utf8JsonWriter) (value: SessionSnapshot) =
        writer.WriteStartObject()
        JsonWrite.property "authenticated" (ProtocolScalar9.write) writer value.Authenticated

        JsonWrite.property
            "antiforgeryToken"
            (JsonWrite.nullable (ProtocolScalar8.write))
            writer
            value.AntiforgeryToken

        writer.WriteEndObject()

module internal SessionResponseOutcomeJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : SessionResponseOutcome =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar49.read) value
            Data = JsonRead.required "data" (SessionSnapshotJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SessionResponseOutcome) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar49.write) writer value.Tag
        JsonWrite.property "data" (SessionSnapshotJson.write) writer value.Data
        writer.WriteEndObject()

module internal SessionResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : SessionResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar48.read) value
            Outcome = JsonRead.required "outcome" (SessionResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SessionResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar48.write) writer value.Endpoint
        JsonWrite.property "outcome" (SessionResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal SessionLoginResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : SessionLoginResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar50.read) value
            Outcome = JsonRead.required "outcome" (SessionResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SessionLoginResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar50.write) writer value.Endpoint
        JsonWrite.property "outcome" (SessionResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal SessionLoginRequestJson =
    let private properties = [ "credential"; "antiforgeryToken" ]

    let read (value: JsonElement) : SessionLoginRequest =
        JsonRead.objectValue properties value

        {
            Credential = JsonRead.required "credential" (ProtocolScalar8.read) value
            AntiforgeryToken = JsonRead.required "antiforgeryToken" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SessionLoginRequest) =
        writer.WriteStartObject()
        JsonWrite.property "credential" (ProtocolScalar8.write) writer value.Credential
        JsonWrite.property "antiforgeryToken" (ProtocolScalar8.write) writer value.AntiforgeryToken
        writer.WriteEndObject()

module internal SessionLogoutResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : SessionLogoutResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar51.read) value
            Outcome = JsonRead.required "outcome" (SessionResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: SessionLogoutResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar51.write) writer value.Endpoint
        JsonWrite.property "outcome" (SessionResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal SessionLogoutRequestJson =
    let private properties = []

    let read (value: JsonElement) : SessionLogoutRequest =
        JsonRead.objectValue properties value
        ()

    let write (writer: Utf8JsonWriter) (_value: SessionLogoutRequest) =
        writer.WriteStartObject()
        writer.WriteEndObject()

module internal DefinitionResponseOutcomeJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : DefinitionResponseOutcome =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar53.read) value
            Data = JsonRead.required "data" (DefinitionPayloadJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefinitionResponseOutcome) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar53.write) writer value.Tag
        JsonWrite.property "data" (DefinitionPayloadJson.write) writer value.Data
        writer.WriteEndObject()

module internal DefinitionResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : DefinitionResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar52.read) value
            Outcome = JsonRead.required "outcome" (DefinitionResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: DefinitionResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar52.write) writer value.Endpoint
        JsonWrite.property "outcome" (DefinitionResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeSucceededDataFoundJson =
    let private properties = [ "tag"; "current" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeSucceededDataFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar44.read) value
            Current = JsonRead.required "current" (CurrentCaseJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeSucceededDataFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar44.write) writer value.Tag
        JsonWrite.property "current" (CurrentCaseJson.write) writer value.Current
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeSucceededDataNotFoundJson =
    let private properties = [ "tag"; "caseReference" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeSucceededDataNotFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar45.read) value
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar8.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeSucceededDataNotFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar45.write) writer value.Tag
        JsonWrite.property "caseReference" (ProtocolScalar8.write) writer value.CaseReference
        writer.WriteEndObject()
