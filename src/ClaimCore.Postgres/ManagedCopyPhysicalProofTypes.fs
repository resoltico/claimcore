namespace ClaimCore.Postgres

open System

/// Signed metadata-only evidence that one exact encrypted BASE/WAL object was physically
/// rechecked. It does not assert cross-cluster recovery or installation readiness.
[<NoEquality; NoComparison>]
type internal ManagedCopyPhysicalProof =
    {
        VerificationEventId: Guid
        Nonce: byte array
        CopyId: Guid
        CopyEventId: Guid
        CopyRevision: int64
        ArchiveObjectId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WitnessCutoffSequence: int64
        WitnessCutoffHash: byte array
        Cluster: string
        Kind: string
        PostgresSystemId: string
        Timeline: int
        WalSegmentBytes: int
        BackupManifestSha256: byte array option
        WalStartLsn: string option
        WalEndLsn: string option
        WalSegment: string option
        LocationCommitment: byte array
        CiphertextSha256: byte array
        CiphertextBytes: int64
        DecryptedSha256: byte array
        DecryptedBytes: int64
        PgVerifyBackupManifestSha256: byte array option
        RecoveredPostgresSystemId: string option
        RecoveredTimeline: int option
        RecoveredInstallationId: Guid option
        RecoveredLineageId: Guid option
        RecoveredEpoch: int64 option
        RecoveredRowCount: int64 option
        WalFirstRecordParsed: bool
        WalTimelineMatched: bool
        PgVerifyBackup: bool
        IsolatedBoot: bool
        RecoveredIdentityChecked: bool
        CiphertextRehashed: bool
        Decrypted: bool
        VerifierSigningKeyId: Guid
        VerifierHolderActorId: Guid
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }
