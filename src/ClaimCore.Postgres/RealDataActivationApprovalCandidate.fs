namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Application

/// Actor-attributed, metadata-only authority action for one stable installation activation plan.
module internal RealDataActivationApprovalCandidate =
    let canonical
        (request: RealDataActivationApprovalRequest)
        (actorId: Guid)
        (grantRevision: int64)
        (approvedAt: DateTimeOffset)
        =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("action", "APPROVE_REAL_DATA_ACTIVATION")
        writer.WriteString("approvalId", request.ApprovalId)
        writer.WriteString("planId", request.PlanId)
        writer.WriteString("activationId", request.ActivationId)
        writer.WriteString("installationId", request.InstallationId)
        writer.WriteString("lineageId", request.LineageId)
        writer.WriteNumber("epoch", request.Epoch)
        writer.WriteNumber("writerGeneration", request.WriterGeneration)

        writer.WriteString(
            "activationPlanSha256",
            Convert.ToHexStringLower(request.ActivationPlanSha256)
        )

        writer.WriteString("policySha256", Convert.ToHexStringLower(request.PolicySha256))
        writer.WriteNumber("reviewWitnessSequence", request.ReviewWitnessSequence)
        writer.WriteString("reviewWitnessHash", Convert.ToHexStringLower(request.ReviewWitnessHash))
        writer.WriteNumber("expectedWitnessSequence", request.ExpectedWitnessSequence)

        writer.WriteString(
            "expectedWitnessHash",
            Convert.ToHexStringLower(request.ExpectedWitnessHash)
        )

        writer.WriteString("approverActorId", actorId)
        writer.WriteNumber("approverGrantRevision", grantRevision)

        writer.WriteString(
            "approvedAt",
            approvedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteString(
            "expiresAt",
            request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()
