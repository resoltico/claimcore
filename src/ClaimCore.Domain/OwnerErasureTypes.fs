namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type OwnerErasureAction =
    | ConfirmManagedPayloadAbsence
    | CompleteSuppressionHorizon

/// A technical schema-owner execution decision, never a fabricated human actor. Storage must
/// derive each approval from a current witnessed DATA_STEWARD grant under the authority lock.
type OwnerErasureDecision =
    {
        Action: OwnerErasureAction
        EventId: Guid
        CaseId: Guid
        ExpectedRevision: int64
        ExpectedAuthorityRevision: int64
        ExpectedAuthorityHash: string
        InstallationId: Guid
        LineageId: Guid
        WitnessEpoch: int64
        PruneEventId: Guid
        WitnessCutoffSequence: int64
        WitnessCutoffHash: string
        CopyInventoryDigest: string
        RelevantCopyCount: int64
        ExpectedWriterGeneration: int64
        RecoveryFenceDigest: string option
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        EventDigest: string
        At: DateTimeOffset
        ValidUntil: DateTimeOffset
        Approvals: OwnerErasureApproval list
    }

and OwnerErasureApproval =
    private
        {
            ApprovalId: Guid
            Action: OwnerErasureAction
            EventId: Guid
            CaseId: Guid
            ExpectedAuthorityRevision: int64
            ExpectedAuthorityHash: string
            EventDigest: string
            PolicyId: string
            SuppressionUntil: DateTimeOffset
            CopyInventoryDigest: string
            RelevantCopyCount: int64
            ExpectedWriterGeneration: int64
            RecoveryFenceDigest: string option
            ApproverId: Guid
            GrantRevision: int64
            WitnessSequence: int64
            WitnessHash: string
            ExpiresAt: DateTimeOffset
        }

type internal CopyAbsenceFacts =
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

type internal RecoveryFenceFacts =
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
