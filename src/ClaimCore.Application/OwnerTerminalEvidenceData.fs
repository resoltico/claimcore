module internal ClaimCore.Application.OwnerTerminalEvidenceData

open System

/// Exact facts only; their presence does not prove that any location is absent. The owner
/// issuer verifies signed evidence before the internal authority mints an opaque Domain seal.
type CopyAbsenceFacts =
    {
        InstallationId: Guid
        LineageId: Guid
        WitnessEpoch: int64
        CaseId: Guid
        PruneEventId: Guid
        CutoffSequence: int64
        CutoffHash: string
        InventoryDigest: string
        RelevantCopyCount: int64
        WriterGeneration: int64
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        VerifiedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

type RecoveryFenceFacts =
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
