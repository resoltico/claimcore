namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

[<RequireQualifiedAccess>]
type VerificationFinding =
    | Link
    | OperationEffectGroup
    | EntryHash
    | Commitment
    | PayloadFormat
    | RevisionSequence
    | HistoryStart
    | StateSequence
    | ErasureIncomplete
    | PayloadWithoutErasure
    | PeopleFold
    | ActorNotEnabled
    | HumanRequired
    | OwnerRequired
    | CaseProjection
    | CasePaymentsProjection
    | CaseStatusProjection
    | CaseProgressProjection
    | CaseChangedAtProjection
    | CaseSearchProjection
    | AttachmentPayloads
    | AttachmentReferences
    | CaseAttachmentsProjection
    | CaseNotesProjection
    | CasePartiesProjection
    | CaseWorkProjection
    | CaseReceivedOnProjection
    | CaseClaimedAmountProjection
    | CaseClaimedCurrencyProjection
    | CasePayableAmountProjection
    | CasePaymentReviewProjection
    | CaseOutcomeProjection
    | CasePaymentTotalsProjection
    | CasePayableCurrencyProjection
    | PeopleProjection
    | PeopleProfileProjection
    | Head
    | CheckpointInstallation
    | ExpiryFrontier
    | ExpectedPosition

type VerificationDivergence =
    {
        Sequence: Sequence option
        Finding: VerificationFinding
    }

[<RequireQualifiedAccess>]
type VerificationResult =
    | Verified of LedgerPosition
    | Diverged of VerificationDivergence

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module VerificationFinding =
    let vocabulary =
        Vocabulary.ofUnion<VerificationFinding> "finding" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

type ProjectionRebuildReport =
    {
        Position: LedgerPosition
        PreviousDivergence: VerificationDivergence option
    }

/// Owner restore admission preserves a trusted prefix and confirms operational expiry atomically.
type RestoreAdmissionReport =
    {
        Checkpoint: InstallationCheckpoint
        InvalidatedSessions: int64
    }
