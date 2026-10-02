namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// An opaque tombstone handle, not a case reference or claimant identifier. The target seal is
/// independently recomputed from the complete witness chain before it can authorize pruning.
type TombstonePruneProposal =
    {
        EventId: Guid
        CaseId: Guid
        PurgeEventId: Guid
        PurgeWitnessSequence: int64
        PurgeWitnessEpoch: int64
        PurgeWitnessHash: string
        CutoffSequence: int64
        CutoffHash: string
        TargetCount: int64
        TargetDigest: string
        ExpectedAuthorityRevision: int64
        ExpectedAuthorityHash: string
        ValidUntil: DateTimeOffset
    }

/// A draft for a steward to approve, not evidence that the copy set is absent. The owner
/// re-derives every field under lock from independent signed evidence before phase advancement.
type TerminalCopyProposal =
    {
        EventId: Guid
        CaseId: Guid
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
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

type TerminalFinalProposal =
    {
        Copy: TerminalCopyProposal
        RecoveryFenceDigest: string
        OldWriterGeneration: int64
        NewWriterGeneration: int64
    }

[<RequireQualifiedAccess>]
type TombstoneTerminalProposal =
    | ConfirmManagedPayloadAbsence of TerminalCopyProposal
    | CompleteSuppressionHorizon of TerminalFinalProposal

module TombstoneTerminalProposal =
    let copy =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence value -> value
        | TombstoneTerminalProposal.CompleteSuppressionHorizon value -> value.Copy

    let eventId value = (copy value).EventId
    let caseId value = (copy value).CaseId

[<RequireQualifiedAccess>]
type TombstoneHoldMutation =
    | Record of holdId: Guid * groundCode: string * reviewOn: DateOnly
    | Release of holdId: Guid * releaseCode: string

type TombstoneHoldChange =
    {
        EventId: Guid
        CaseId: Guid
        ExpectedAuthorityRevision: int64
        ExpectedAuthorityHash: string
        Mutation: TombstoneHoldMutation
    }

type TombstoneHoldSummary = { HoldId: Guid; ReviewOn: DateOnly }

type TombstoneReview =
    {
        CaseId: Guid
        PrivacyPhase: PrivacyPhase
        PurgeEventId: Guid
        PurgeWitnessSequence: int64
        PurgeWitnessEpoch: int64
        PurgeWitnessHash: string
        CutoffSequence: int64
        CutoffHash: string
        TargetCount: int64
        TargetDigest: string
        AuthorityRevision: int64
        AuthorityHash: string
        ActiveHolds: TombstoneHoldSummary list
        RequiredDistinctStewardApprovals: int
        WitnessPayloadPruned: bool
        ManagedCopyCertificationPending: bool
    }

[<RequireQualifiedAccess>]
type TombstoneReviewOutcome =
    | Available of TombstoneReview
    | ResourceUnavailable
    | Cancelled
    | Failed of CoreFault

[<RequireQualifiedAccess>]
type TombstoneWriteOutcome =
    | Applied of eventId: Guid * authorityRevision: int64
    | Refused of LifecycleRefusal
    | ResourceUnavailable
    | CancelledBeforeAdmission of eventId: Guid
    | Failed of CoreFault
    | Unconfirmed of eventId: Guid

/// Actor-bound, non-disclosing review and fresh approvals; owner-only prune execution is not
/// exposed here. Under-lock grant and hold checks remain in the storage implementation.
type ITombstoneWorkflow =
    abstract Review:
        caseId: Guid * cancellationToken: CancellationToken -> Task<TombstoneReviewOutcome>

    abstract ApproveWitnessPrune:
        proposal: TombstonePruneProposal *
        approvalId: Guid *
        expiresAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<TombstoneWriteOutcome>

    abstract ApproveTerminal:
        proposal: TombstoneTerminalProposal *
        approvalId: Guid *
        expiresAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<TombstoneWriteOutcome>

    abstract ChangeHold:
        change: TombstoneHoldChange * cancellationToken: CancellationToken ->
            Task<TombstoneWriteOutcome>

type internal ITombstoneStore =
    abstract Review: actor: ActorCallContext * caseId: Guid -> Task<TombstoneReviewOutcome>

    abstract ApproveWitnessPrune:
        actor: ActorCallContext *
        proposal: TombstonePruneProposal *
        approvalId: Guid *
        expiresAt: DateTimeOffset ->
            Task<TombstoneWriteOutcome>

    abstract ApproveTerminal:
        actor: ActorCallContext *
        proposal: TombstoneTerminalProposal *
        approvalId: Guid *
        expiresAt: DateTimeOffset ->
            Task<TombstoneWriteOutcome>

    abstract ChangeHold:
        actor: ActorCallContext * change: TombstoneHoldChange -> Task<TombstoneWriteOutcome>
