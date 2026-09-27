namespace ClaimCore.Postgres

open System

[<NoEquality; NoComparison>]
type internal BackupHealthBase =
    {
        CopyId: Guid
        Revision: int64
        PhysicalReceiptSha256: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthWal =
    {
        CopyIds: Guid list
        RegisteredHorizon: string
        ArchiveInspectionSha256: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthCheckpoint =
    {
        Sequence: int64
        Hash: string
        ObjectSha256: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthTestRestore =
    {
        ReportSha256: string
        WitnessCutoff: int64
        WitnessCutoffHash: string
        VerifiedAt: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal BackupHealthWriterFence =
    {
        Kind: string
        HandoffId: Guid option
        W1Sequence: int64 option
        W1Hash: string option
        ActivationSequence: int64 option
        ActivationHash: string option
        OldGeneration: int64 option
        NewGeneration: int64 option
    }

[<NoEquality; NoComparison>]
type internal BackupHealthClaims =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        PolicyId: string
        AuthorityRevision: int64
        WitnessTipSequence: int64
        WitnessTipHash: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        MaximumBackupAgeSeconds: int64
        MaximumWalLagSeconds: int64
        MaximumCheckpointAgeSeconds: int64
        MaximumRestoreTestAgeSeconds: int64
        RestoreHorizonSeconds: int64
        PrimarySystemId: string
        PrimaryTimeline: int64
        WitnessSystemId: string
        WitnessTimeline: int64
        PrimaryBase: BackupHealthBase
        WitnessBase: BackupHealthBase
        PrimaryWal: BackupHealthWal
        WitnessWal: BackupHealthWal
        Checkpoint: BackupHealthCheckpoint
        TestRestore: BackupHealthTestRestore
        KnownCopyInventorySha256: string
        ArtifactCutoffSequence: int64
        WriterFence: BackupHealthWriterFence
        SignerKeyId: Guid
        SignerHolderActorId: Guid
    }

[<NoEquality; NoComparison>]
type internal BackupHealthRolePin =
    {
        Role: string
        PublicKey: byte array
        MachineSha256: string
        StorageSha256: string
        AdminActorId: Guid
        SignerHolderActorId: Guid
    }

[<NoEquality; NoComparison>]
type internal BackupHealthPolicy =
    {
        PolicyId: string
        BackupIntervalSeconds: int64
        MaximumBackupAgeSeconds: int64
        MaximumWalLagSeconds: int64
        MaximumCheckpointAgeSeconds: int64
        MaximumRestoreTestAgeSeconds: int64
        RestoreHorizonSeconds: int64
        ArchiveRoot: string
        CheckpointRoot: string
        RestoreRoot: string
        Roles: BackupHealthRolePin list
    }
