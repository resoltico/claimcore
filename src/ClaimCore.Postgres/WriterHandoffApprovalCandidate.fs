namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Application

/// Exact metadata-only owner approval for one signed writer cutover fence.
module internal WriterHandoffApprovalCandidate =
    let canonical (request: WriterHandoffApprovalRequest) (actorId: Guid) (grantRevision: int64) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "APPROVE_WRITER_HANDOFF")
        writer.WriteString("approvalId", request.ApprovalId)
        writer.WriteString("handoffId", request.HandoffId)
        writer.WriteNumber("oldGeneration", request.OldGeneration)
        writer.WriteNumber("expectedWitnessSequence", request.ExpectedWitnessSequence)

        writer.WriteString(
            "expectedWitnessHash",
            Convert.ToHexStringLower(request.ExpectedWitnessHash)
        )

        writer.WriteString(
            "newCapabilitySha256",
            Convert.ToHexStringLower(request.NewCapabilitySha256)
        )

        writer.WriteString("checkpointSigningKeyId", request.CheckpointSigningKeyId)
        writer.WriteString("fenceReportSha256", Convert.ToHexStringLower(request.FenceReportSha256))
        writer.WriteString("inventorySha256", Convert.ToHexStringLower(request.InventorySha256))
        writer.WriteString("approverActorId", actorId)
        writer.WriteNumber("approverGrantRevision", grantRevision)

        writer.WriteString(
            "expiresAt",
            request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let private digest32 (value: byte array) =
        not (isNull (box value)) && value.Length = 32

    let valid (context: ActorCallContext) (request: WriterHandoffApprovalRequest) =
        context.Action = EndpointAction.ApproveWriterHandoff
        && context.CaseId.IsNone
        && PrincipalKey.isHuman context.Binding.Principal
        && request.ApprovalId <> Guid.Empty
        && request.HandoffId <> Guid.Empty
        && request.CheckpointSigningKeyId <> Guid.Empty
        && request.OldGeneration > 0L
        && request.ExpectedWitnessSequence >= 0L
        && digest32 request.ExpectedWitnessHash
        && digest32 request.NewCapabilitySha256
        && digest32 request.FenceReportSha256
        && digest32 request.InventorySha256
        && Sql.isUtcMicrosecond request.ExpiresAt
