namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<NoEquality; NoComparison>]
type internal InstallationUseApprovalEvidence =
    {
        ApprovalId: Guid
        ActorId: Guid
        GrantRevision: int64
        IntentSequence: int64
        IntentHash: byte array
        SettlementSequence: int64
        SettlementHash: byte array
        ExpiresAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal InstallationUseApprovalPair =
    {
        First: InstallationUseApprovalEvidence
        Second: InstallationUseApprovalEvidence
        ReviewSequence: int64
        ReviewHash: byte array
    }

/// Deterministic one-shot installation authority candidate; a retry cannot mint a new identity.
module internal InstallationUseActivationCandidate =
    let private domain = Encoding.ASCII.GetBytes("claimcore-real-data-activation-v1:")

    let private planDomain =
        Encoding.ASCII.GetBytes("claimcore-real-data-plan-publication-v1:")

    let private deriveId tag (installationId: Guid) (planSha256: byte array) =
        if
            installationId = Guid.Empty
            || isNull (box planSha256)
            || planSha256.Length <> 32
        then
            invalidArg (nameof planSha256) "Activation plan identity is invalid."

        let input = Array.concat [ tag; installationId.ToByteArray(); planSha256 ]
        let hash = SHA256.HashData(input)
        let bytes = hash.AsSpan(0, 16).ToArray()
        bytes[6] <- (bytes[6] &&& 0x0fuy) ||| 0x80uy
        bytes[8] <- (bytes[8] &&& 0x3fuy) ||| 0x80uy
        CryptographicOperations.ZeroMemory(input)
        Guid(bytes)

    let eventIdFromPlan installationId planSha256 =
        deriveId domain installationId planSha256

    let planIdFromDigest installationId planSha256 =
        deriveId planDomain installationId planSha256

    let private writeApproval
        (writer: Utf8JsonWriter)
        prefix
        (value: InstallationUseApprovalEvidence)
        =
        writer.WriteString(prefix + "Id", value.ApprovalId)
        writer.WriteString(prefix + "ActorId", value.ActorId)
        writer.WriteNumber(prefix + "GrantRevision", value.GrantRevision)
        writer.WriteNumber(prefix + "IntentSequence", value.IntentSequence)
        writer.WriteString(prefix + "IntentHash", Convert.ToHexStringLower value.IntentHash)
        writer.WriteNumber(prefix + "SettlementSequence", value.SettlementSequence)
        writer.WriteString(prefix + "SettlementHash", Convert.ToHexStringLower value.SettlementHash)
        writer.WriteString(prefix + "ExpiresAt", value.ExpiresAt.ToUniversalTime().ToString("O"))

    let encodeFinal
        (plan: BackupHealthActivationPlan)
        (proof: BackupHealthQualifiedEvidence)
        (approvals: InstallationUseApprovalPair)
        =
        let id = eventIdFromPlan plan.InstallationId (Convert.FromHexString plan.PlanSha256)

        let planId =
            planIdFromDigest plan.InstallationId (Convert.FromHexString plan.PlanSha256)

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "ACTIVATE_REAL_DATA")
        writer.WriteString("eventId", id)
        writer.WriteString("planId", planId)
        writer.WriteString("installationId", plan.InstallationId)
        writer.WriteString("lineageId", plan.LineageId)
        writer.WriteNumber("epoch", plan.Epoch)
        writer.WriteNumber("writerGeneration", plan.WriterGeneration)
        writer.WriteString("dataUseScope", "REAL_DATA")
        writer.WriteString("fromPhase", "BOOTSTRAP_NO_CASES")
        writer.WriteString("toPhase", "ACTIVE")
        writer.WriteString("activationPlanSha256", plan.PlanSha256)
        writer.WriteString("policySha256", plan.PolicySha256)
        writer.WriteString("healthCertificateSha256", proof.CertificateSha256)
        writer.WriteNumber("expectedWitnessSequence", proof.WitnessTipSequence)
        writer.WriteString("expectedWitnessHash", Convert.ToHexStringLower proof.WitnessTipHash)

        writer.WriteString(
            "knownCopyInventorySha256",
            Convert.ToHexStringLower proof.KnownCopyInventorySha256
        )

        writer.WriteString("healthSignerKeyId", proof.SignerKeyId)
        writer.WriteString("healthSignerHolderActorId", proof.SignerHolderActorId)

        writer.WriteString(
            "healthCheckedAt",
            proof.CheckedAtDatabase.ToUniversalTime().ToString("O")
        )

        writer.WriteString("healthValidUntil", proof.ValidUntil.ToUniversalTime().ToString("O"))
        writer.WriteNumber("reviewWitnessSequence", approvals.ReviewSequence)
        writer.WriteString("reviewWitnessHash", Convert.ToHexStringLower approvals.ReviewHash)

        writeApproval writer "approvalOne" approvals.First
        writeApproval writer "approvalTwo" approvals.Second
        writer.WriteEndObject()
        writer.Flush()
        let canonical = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        canonical
