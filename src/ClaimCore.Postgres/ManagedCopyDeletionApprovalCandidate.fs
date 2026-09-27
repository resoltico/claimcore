namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Application

/// Metadata-only, exact-byte approval candidate for one deletion report and copy revision.
module internal ManagedCopyDeletionApprovalCandidate =
    let canonical (request: CopyDeletionApprovalRequest) (actorId: Guid) (grantRevision: int64) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "APPROVE_COPY_DELETION")
        writer.WriteString("approvalId", request.ApprovalId)
        writer.WriteString("deletionEventId", request.DeletionEventId)
        writer.WriteString("copyId", request.CopyId)
        writer.WriteString("verifierSigningKeyId", request.VerifierSigningKeyId)
        writer.WriteNumber("expectedCopyRevision", request.ExpectedCopyRevision)

        writer.WriteString(
            "locationCommitment",
            Convert.ToHexStringLower(request.LocationCommitment)
        )

        writer.WriteString(
            "inspectionReportSha256",
            Convert.ToHexStringLower(request.InspectionReportSha256)
        )

        writer.WriteNumber("witnessCutoffSequence", request.WitnessCutoffSequence)
        writer.WriteString("witnessCutoffHash", Convert.ToHexStringLower(request.WitnessCutoffHash))
        writer.WriteString("approverActorId", actorId)
        writer.WriteNumber("approverGrantRevision", grantRevision)

        writer.WriteString(
            "expiresAt",
            request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()
