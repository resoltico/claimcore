namespace ClaimCore.Postgres

open System

[<NoEquality; NoComparison>]
type internal ManagedCopyAttestation =
    {
        EventId: Guid
        CopyId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        Cluster: string
        Kind: string
        SourceCaseId: Guid option
        PostgresSystemId: string option
        Timeline: int option
        WalSegmentBytes: int option
        BackupManifestSha256: byte array option
        WalStartLsn: string option
        WalEndLsn: string option
        WalSegment: string option
        WitnessCutoffSequence: int64
        WitnessCutoffHash: byte array
        CiphertextSha256: byte array
        CiphertextBytes: int64
        EncryptionKeyId: Guid
        SigningKeyId: Guid
        CustodianCommitment: byte array
        LocationCommitment: byte array
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        VerificationProofSha256: byte array option
        CycleId: Guid option
    }

[<NoEquality; NoComparison>]
type internal ManagedCopyTransition =
    {
        Copy: ManagedCopyAttestation
        Revision: int64
        EventKind: string
        State: string
        PreviousEventHash: byte array
        ActionWitnessCutoffSequence: int64
        ActionWitnessCutoffHash: byte array
        LastVerifiedAt: DateTimeOffset option
        DeletionProofSha256: byte array option
        DeletionApprovalId: Guid option
    }
