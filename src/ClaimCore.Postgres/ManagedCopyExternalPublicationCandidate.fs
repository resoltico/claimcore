namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

[<NoEquality; NoComparison>]
type internal ExternalCopyPublicationEvidence =
    {
        Registry: ExternalCopyPublication
        Inspection: ExternalCopyInspection
        Submission: ExternalCopyPublicationSubmission
        RegistryHolderId: Guid
        InspectorHolderId: Guid
        ActorAuthorityRevision: int64
        CaseRevision: int64
        ObservedAt: DateTimeOffset
        PrivateLocationExpiresAt: DateTimeOffset
    }

/// Metadata-only owner authority; raw mapped path and claimant bytes are not journaled.
module internal ManagedCopyExternalPublicationCandidate =
    let private hex (bytes: byte array) = Convert.ToHexStringLower bytes

    let private signed (writer: Utf8JsonWriter) (value: ExternalCopyPublicationEvidence) =
        writer.WriteString("registrySigningKeyId", value.Registry.RegistrySigningKeyId)
        writer.WriteString("registryHolderActorId", value.RegistryHolderId)
        writer.WriteString("inspectorSigningKeyId", value.Inspection.InspectorSigningKeyId)
        writer.WriteString("inspectorHolderActorId", value.InspectorHolderId)

        writer.WriteString(
            "registryCanonicalSha256",
            hex (SHA256.HashData value.Submission.Registry.Canonical)
        )

        writer.WriteString(
            "registrySignatureSha256",
            hex (SHA256.HashData value.Submission.Registry.Signature)
        )

        writer.WriteString(
            "inspectionCanonicalSha256",
            hex (SHA256.HashData value.Submission.Inspection.Canonical)
        )

        writer.WriteString(
            "inspectionSignatureSha256",
            hex (SHA256.HashData value.Submission.Inspection.Signature)
        )

    let encode (value: ExternalCopyPublicationEvidence) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        let publication = value.Registry
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "PUBLISH_EXTERNAL_COPY")
        writer.WriteString("executorKind", "SCHEMA_OWNER_PROCESS")
        writer.WriteString("publicationId", publication.PublicationId)
        writer.WriteString("copyId", publication.CopyId)
        writer.WriteString("caseId", publication.CaseId)
        writer.WriteString("installationId", publication.InstallationId)
        writer.WriteString("lineageId", publication.LineageId)
        writer.WriteNumber("epoch", publication.Epoch)
        writer.WriteString("encryptionKeyId", publication.EncryptionKeyId)
        writer.WriteString("ciphertextSha256", hex publication.CiphertextSha256)
        writer.WriteNumber("ciphertextBytes", publication.CiphertextBytes)
        writer.WriteString("capturedAt", publication.CapturedAt.ToString("O"))
        writer.WriteString("retainUntil", publication.RetainUntil.ToString("O"))
        writer.WriteString("locationCommitment", hex publication.LocationCommitment)
        writer.WriteString("custodianCommitment", hex publication.CustodianCommitment)
        signed writer value
        writer.WriteNumber("actorAuthorityRevision", value.ActorAuthorityRevision)
        writer.WriteNumber("caseRevision", value.CaseRevision)
        writer.WriteString("observedAt", value.ObservedAt.ToString("O"))
        writer.WriteString("validUntil", publication.ValidUntil.ToString("O"))
        writer.WriteString("privateLocationExpiresAt", value.PrivateLocationExpiresAt.ToString("O"))
        writer.WriteEndObject()
        writer.Flush()
        buffer.ToArray()
