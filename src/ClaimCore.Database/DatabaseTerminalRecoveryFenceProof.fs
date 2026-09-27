namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal TerminalRecoveryFenceFacts =
    {
        InstallationId: Guid
        LineageId: Guid
        WitnessEpoch: int64
        CaseId: Guid
        OldWriterGeneration: int64
        NewWriterGeneration: int64
        CopyInventoryDigest: string
        FenceDigest: string
        AuthorityRevision: int64
        AuthorityHash: string
        WitnessSettlementSequence: int64
        WitnessSettlementHash: string
        ArtifactCutoffSequence: int64
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        VerifiedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// Nonpayload digest of the exact previously witnessed W2 fence, current copy absence and
/// exhausted recovery artifacts. The underlying signed evidence remains owner-private.
module internal DatabaseTerminalRecoveryFenceProof =
    let encode
        (facts: TerminalRecoveryFenceFacts)
        (candidate: WriterActivationEvidence)
        (copy: OwnerCopyAbsenceCertificate)
        (row: TerminalRecoveryFenceRow)
        =
        let activationCanonical = WriterActivationCandidate.encode candidate

        let activationDigest =
            try
                SHA256.HashData(activationCanonical) |> Convert.ToHexStringLower
            finally
                CryptographicOperations.ZeroMemory(activationCanonical)

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("kind", "TERMINAL_RECOVERY_FENCE")
        writer.WriteString("installationId", facts.InstallationId)
        writer.WriteString("lineageId", facts.LineageId)
        writer.WriteNumber("epoch", facts.WitnessEpoch)
        writer.WriteString("caseId", facts.CaseId)
        writer.WriteNumber("oldWriterGeneration", facts.OldWriterGeneration)
        writer.WriteNumber("newWriterGeneration", facts.NewWriterGeneration)
        writer.WriteString("copyInventoryDigest", facts.CopyInventoryDigest)

        writer.WriteString(
            "copyAbsenceProofSha256",
            Convert.ToHexStringLower copy.SignedProofSha256
        )

        writer.WriteString("fenceDigest", facts.FenceDigest)
        writer.WriteString("activationId", row.ActivationId)
        writer.WriteString("activationCandidateSha256", activationDigest)
        writer.WriteNumber("authorityRevision", facts.AuthorityRevision)
        writer.WriteString("authorityHash", facts.AuthorityHash)
        writer.WriteNumber("witnessSettlementSequence", facts.WitnessSettlementSequence)
        writer.WriteString("witnessSettlementHash", facts.WitnessSettlementHash)
        writer.WriteNumber("artifactCutoffSequence", facts.ArtifactCutoffSequence)
        writer.WriteString("policyId", facts.PolicyId)

        writer.WriteString(
            "suppressionUntil",
            facts.SuppressionUntil.ToUniversalTime().ToString("O")
        )

        writer.WriteString("verifiedAt", facts.VerifiedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("validUntil", facts.ValidUntil.ToUniversalTime().ToString("O"))
        writer.WriteEndObject()
        writer.Flush()
        let canonical = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        canonical
