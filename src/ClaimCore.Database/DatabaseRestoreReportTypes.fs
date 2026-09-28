namespace ClaimCore.Database

open System

[<NoEquality; NoComparison>]
type internal RestoreCustodyObject =
    {
        ObjectId: Guid
        Sha256: string
        Bytes: int64
    }

[<NoEquality; NoComparison>]
type internal RestoreReportClaims =
    {
        Scope: string
        RealDataReady: bool
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        CycleId: Guid
        BackupCaptureSequence: int64
        BackupCaptureHash: string
        WitnessCutoff: int64
        WitnessCutoffHash: string
        PrimarySystemId: string
        PrimaryTimeline: int64
        WitnessSystemId: string
        WitnessTimeline: int64
        PrimaryRegisteredWalHorizon: string
        WitnessRegisteredWalHorizon: string
        SignerKeyId: Guid
        VerifierBinarySha256: string
        EvidenceIndexSha256: string
        CheckpointSha256: string
        SignedInventoryFileSha256: string
        QuiescentBarrierSha256: string
        CatalogManifestSha256: string
        AuthorityRevision: int64
        AuthorizedApprovers: RestoreOwnerApprover list
        ArchiveCustody: RestoreCustodyObject
        CheckpointCustody: RestoreCustodyObject
        CustodyKeyId: Guid
        CustodyPublicKeySha256: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }
