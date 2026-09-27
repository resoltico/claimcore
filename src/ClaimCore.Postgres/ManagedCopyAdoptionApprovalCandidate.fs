namespace ClaimCore.Postgres

open System
open System.IO
open System.Text.Json
open ClaimCore.Application

/// Exact metadata-only owner approval. The actor never receives a raw location, signature,
/// private recovery artifact or schema-owner credential; signing evidence is checked later.
module internal ManagedCopyAdoptionApprovalCandidate =
    let private digest (value: byte array) = Convert.ToHexStringLower value

    let canonical
        (request: CopyAdoptionApprovalRequest)
        (actorId: Guid)
        (grantRevision: int64)
        (approvedAt: DateTimeOffset)
        =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "APPROVE_COPY_ADOPTION")
        writer.WriteString("approvalId", request.ApprovalId)
        writer.WriteString("adoptionEventId", request.AdoptionEventId)
        writer.WriteString("copyId", request.CopyId)
        writer.WriteString("caseId", request.CaseId)

        match request.Origin with
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            writer.WriteString("originKind", "PRODUCT_EXPORT")
            writer.WriteString("exportId", exportId)
            writer.WriteNumber("preFenceSequence", sequence)
            writer.WriteString("preFenceHash", digest hash)
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            writer.WriteString("originKind", "ADOPTED_EXTERNAL")
            writer.WriteNull("exportId")
            writer.WriteNumber("preFenceSequence", sequence)
            writer.WriteString("preFenceHash", digest hash)

        writer.WriteString("ciphertextSha256", digest request.CiphertextSha256)
        writer.WriteNumber("ciphertextBytes", request.CiphertextBytes)
        writer.WriteString("capturedAt", request.CapturedAt.ToString("O"))
        writer.WriteString("retainUntil", request.RetainUntil.ToString("O"))
        writer.WriteString("locationCommitment", digest request.LocationCommitment)
        writer.WriteString("custodianCommitment", digest request.CustodianCommitment)
        writer.WriteString("custodianSigningKeyId", request.CustodianSigningKeyId)
        writer.WriteString("registrySigningKeyId", request.RegistrySigningKeyId)
        writer.WriteString("inspectorSigningKeyId", request.InspectorSigningKeyId)
        writer.WriteString("custodianCanonicalSha256", digest request.CustodianCanonicalSha256)
        writer.WriteString("registryCanonicalSha256", digest request.RegistryCanonicalSha256)
        writer.WriteString("inspectionReportSha256", digest request.InspectionReportSha256)
        writer.WriteString("approvedAt", approvedAt.ToString("O"))
        writer.WriteString("expiresAt", request.ExpiresAt.ToString("O"))
        writer.WriteString("ownerActorId", actorId)
        writer.WriteNumber("ownerGrantRevision", grantRevision)
        writer.WriteEndObject()
        writer.Flush()
        buffer.ToArray()
