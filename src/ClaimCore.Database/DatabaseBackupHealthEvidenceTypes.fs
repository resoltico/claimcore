namespace ClaimCore.Database

open System

[<NoEquality; NoComparison>]
type internal BackupHealthArchiveObject =
    {
        CopyId: Guid
        Revision: int64
        Cluster: string
        Kind: string
        PostgresSystemId: string
        Timeline: int64
        WalSegmentBytes: int
        WalHorizon: string
        WalSegment: string option
        CiphertextSha256: string
        CiphertextBytes: int64
        PhysicalReceiptSha256: string
        RelativePath: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthCheckpointObject =
    {
        Sequence: int64
        Hash: string
        ObjectSha256: string
        ObjectBytes: int64
        RelativePath: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthRestoredPair =
    {
        ReportSha256: string
        ReportRelativePath: string
        FullAuditSha256: string
        AuditRelativePath: string
        WitnessCutoff: int64
        WitnessCutoffHash: string
        PrimaryBaseCopyId: Guid
        WitnessBaseCopyId: Guid
        PrimaryWalHorizon: string
        WitnessWalHorizon: string
        PrimarySystemId: string
        PrimaryTimeline: int64
        WitnessSystemId: string
        WitnessTimeline: int64
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthEvidence =
    {
        CycleId: Guid
        LeaseId: Guid
        CaptureNonce: string
        CaptureReceiptSha256: string
        BackupCaptureSequence: int64
        BackupCaptureHash: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        PolicyId: string
        AuthorityRevision: int64
        WitnessTipSequence: int64
        WitnessTipHash: string
        KnownCopyInventorySha256: string
        ArtifactCutoffSequence: int64
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        Objects: BackupHealthArchiveObject list
        Checkpoint: BackupHealthCheckpointObject
        TestRestore: BackupHealthRestoredPair
    }
