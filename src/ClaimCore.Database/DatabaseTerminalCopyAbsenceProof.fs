namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

[<NoEquality; NoComparison>]
type internal TerminalCopyAbsenceFacts =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        CaseId: Guid
        PruneEventId: Guid
        CutoffSequence: int64
        CutoffHash: string
        InventoryDigest: string
        CopyCount: int64
        WriterGeneration: int64
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        VerifiedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// Data-minimal digest of the actual signed registry, independent ABSENT inspection, exact
/// deletion receipts and current witness tip. No private path or claimant bytes enter it.
module internal DatabaseTerminalCopyAbsenceProof =
    let private signatures (writer: Utf8JsonWriter) (evidence: SignedCopyLocationEvidence) =
        writer.WriteString(
            "registryCanonicalSha256",
            Convert.ToHexStringLower evidence.RegistrySha256
        )

        writer.WriteString(
            "registrySignatureSha256",
            Convert.ToHexStringLower(SHA256.HashData(evidence.RegistrySignature))
        )

        writer.WriteString(
            "inspectionCanonicalSha256",
            Convert.ToHexStringLower(SHA256.HashData(evidence.InspectionCanonical))
        )

        writer.WriteString(
            "inspectionSignatureSha256",
            Convert.ToHexStringLower(SHA256.HashData(evidence.InspectionSignature))
        )

    let encode
        (facts: TerminalCopyAbsenceFacts)
        (evidence: SignedCopyLocationEvidence)
        (registryHolder: Guid)
        (verifierHolder: Guid)
        (deletionDigest: byte array)
        (tipSequence: int64)
        (tipHash: byte array)
        =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "TERMINAL_COPY_ABSENCE")
        writer.WriteString("installationId", facts.InstallationId)
        writer.WriteString("lineageId", facts.LineageId)
        writer.WriteNumber("epoch", facts.Epoch)
        writer.WriteString("caseId", facts.CaseId)
        writer.WriteString("pruneEventId", facts.PruneEventId)
        writer.WriteNumber("cutoffSequence", facts.CutoffSequence)
        writer.WriteString("cutoffHash", facts.CutoffHash)
        writer.WriteString("inventoryDigest", facts.InventoryDigest)
        writer.WriteNumber("copyCount", facts.CopyCount)
        writer.WriteNumber("writerGeneration", facts.WriterGeneration)
        writer.WriteString("policyId", facts.PolicyId)
        writer.WriteString("suppressionUntil", facts.SuppressionUntil.ToString("O"))
        writer.WriteString("verifiedAt", facts.VerifiedAt.ToString("O"))
        writer.WriteString("validUntil", facts.ValidUntil.ToString("O"))
        writer.WriteNumber("witnessTipSequence", tipSequence)
        writer.WriteString("witnessTipHash", Convert.ToHexStringLower tipHash)
        writer.WriteString("registryHolderActorId", registryHolder)
        writer.WriteString("verifierHolderActorId", verifierHolder)

        signatures writer evidence

        writer.WriteString("deletionReceiptsSha256", Convert.ToHexStringLower deletionDigest)
        writer.WriteEndObject()
        writer.Flush()
        buffer.ToArray()
