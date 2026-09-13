namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application

module internal CliWireImports =
    let private artifactKind (value: RecoveryArtifactKind) =
        match value with
        | RecoveryArtifactKind.Envelope -> "ENVELOPE"
        | RecoveryArtifactKind.UnboundCanonicalRecord -> "CANONICAL_RECORD"

    let private effect (writer: Utf8JsonWriter) (value: RecoveryImportEffect) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WriteString("caseReference", value.CaseReference)
        writer.WriteString("command", CliWireValues.command value.Command)
        writer.WriteString("expectedRevision", value.ExpectedVersion.ToString())
        writer.WriteStartObject("authoredValues")

        value.AuthoredValues
        |> List.iter (fun (name, text) -> writer.WriteString(name, text))

        writer.WriteEndObject()
        writer.WriteNumber("canonicalCommandFormat", value.CanonicalCommandFormat)
        writer.WriteString("requestSha256", value.RequestSha256)
        writer.WriteEndObject()

    let private previewValue (writer: Utf8JsonWriter) (value: RecoveryImportPreview) =
        writer.WriteStartObject()
        writer.WriteString("artifactKind", artifactKind value.ArtifactKind)
        writer.WriteString("sourceSha256", value.SourceSha256)
        writer.WritePropertyName("decodedEffect")
        effect writer value.DecodedEffect
        writer.WritePropertyName("existingPreparation")

        match value.ExistingPreparation with
        | Some summary -> CliWireValues.preparationSummary writer summary
        | None -> writer.WriteNullValue()

        writer.WriteEndObject()

    let private detailsOutcome (writer: Utf8JsonWriter) (kind: string) (value: PreparationDetails) =
        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writer.WritePropertyName("details")
        CliWireValues.preparationDetails writer value
        writer.WriteEndObject()

    let preview (writer: Utf8JsonWriter) (outcome: RecoveryQueryOutcome<RecoveryImportPreview>) =
        match outcome with
        | RecoveryQueryOutcome.RecoverySucceeded value ->
            writer.WriteStartObject()
            writer.WriteString("kind", "previewed")
            writer.WritePropertyName("preview")
            previewValue writer value
            writer.WriteEndObject()
        | RecoveryQueryOutcome.RecoveryRejected rejection ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WritePropertyName("rejection")
            CliWireValues.recoveryRejection writer rejection
            writer.WriteEndObject()
        | RecoveryQueryOutcome.RecoveryFailed fault -> CliWireQueries.failed writer fault
        | RecoveryQueryOutcome.RecoveryCancelled -> CliWireQueries.cancelled writer

    let retain (writer: Utf8JsonWriter) (outcome: RecoveryImportRetainOutcome) =
        match outcome with
        | RecoveryImportRetainOutcome.RetainedPreparation details ->
            detailsOutcome writer "retained" details
        | RecoveryImportRetainOutcome.ExistingPreparation details ->
            detailsOutcome writer "existing" details
        | RecoveryImportRetainOutcome.ImportRejected rejection ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WritePropertyName("rejection")
            CliWireValues.recoveryRejection writer rejection
            writer.WriteEndObject()
        | RecoveryImportRetainOutcome.ImportFailed fault -> CliWireQueries.failed writer fault
        | RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission ->
            writer.WriteStartObject()
            writer.WriteString("kind", "cancelledBeforeAdmission")
            writer.WriteEndObject()
        | RecoveryImportRetainOutcome.RetainStateUnknown(kind, digest, operationId, fault) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "retainStateUnknown")
            writer.WriteString("artifactKind", artifactKind kind)
            writer.WriteString("sourceSha256", digest)

            match operationId with
            | Some value -> writer.WriteString("operationId", value)
            | None -> writer.WriteNull("operationId")

            writer.WritePropertyName("fault")
            CliWireValues.fault writer fault
            writer.WriteEndObject()
