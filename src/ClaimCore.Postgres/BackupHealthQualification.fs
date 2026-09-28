namespace ClaimCore.Postgres

open System

/// Short-lived owner-verified evidence for the one-way real-data activation protocol.
/// A caller Boolean or digest alone cannot substitute for the signed canonical bytes.
[<NoEquality; NoComparison>]
type internal BackupHealthQualifiedEvidence =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        PolicySha256: string
        PolicyCanonical: byte array
        CertificateSha256: string
        WitnessTipSequence: int64
        WitnessTipHash: byte array
        KnownCopyInventorySha256: byte array
        CheckedAtDatabase: DateTimeOffset
        ValidUntil: DateTimeOffset
        SignerKeyId: Guid
        SignerHolderActorId: Guid
        Canonical: byte array
        Signature: byte array
    }

/// An approval binds durable physical recovery facts and minimum WAL coverage, not a
/// short-lived health certificate or a live authority tip that will advance.
[<NoEquality; NoComparison>]
type internal BackupHealthActivationPlan =
    {
        Canonical: byte array
        PlanSha256: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        PolicySha256: string
        PublicationRootSha256: string
        CycleId: Guid
        LeaseId: Guid
        CaptureReceiptSha256: string
        PrimaryBaseCopyId: Guid
        WitnessBaseCopyId: Guid
        PrimaryBasePhysicalReceiptSha256: string
        WitnessBasePhysicalReceiptSha256: string
        PrimaryBaseWalHorizon: string
        WitnessBaseWalHorizon: string
        PrimaryWalSegmentBytes: int
        WitnessWalSegmentBytes: int
        CheckpointObjectSha256: string
        TestRestoreReportSha256: string
        TestRestoreFullAuditSha256: string
        MinimumArtifactCutoffSequence: int64
        MinimumPrimaryWalHorizon: string
        MinimumWitnessWalHorizon: string
    }
