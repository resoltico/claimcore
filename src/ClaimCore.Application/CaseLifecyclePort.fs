namespace ClaimCore.Application

open System
open System.Threading.Tasks
open ClaimCore.Domain

/// A stable caller-authored event identity and one exact proposed lifecycle action.
/// The store resolves the reference to the opaque case ID and checks the actor again under lock.
[<RequireQualifiedAccess>]
type LifecycleMutation =
    | VoidDataEntryError of reason: string
    | ReinstateVoided of reason: string
    | RequestErasure of reason: string
    | MarkErasurePending of reason: string
    | PurgeLivePayload of reason: string * validUntil: DateTimeOffset
    | RecordHold of holdId: Guid * ground: string * reviewOn: DateOnly
    | ReleaseHold of holdId: Guid * reason: string

type LifecycleChange =
    {
        EventId: Guid
        CaseReference: string
        ExpectedRevision: int64
        ExpectedLifecycleSequence: int64
        ExpectedLifecycleHash: string
        Action: LifecycleMutation
    }

type LifecycleHoldSummary = { HoldId: Guid; ReviewOn: DateOnly }

type LifecycleReview =
    {
        BusinessRevision: int64
        LifecycleSequence: int64
        LifecycleHash: string
        Disposition: CaseDisposition
        PrivacyPhase: PrivacyPhase
        ActiveHolds: LifecycleHoldSummary list
        VoidRequiresTwoApprovals: bool
    }

[<RequireQualifiedAccess>]
type LifecycleReviewOutcome =
    | Available of LifecycleReview
    | ResourceUnavailable
    | Cancelled
    | Failed of CoreFault

[<RequireQualifiedAccess>]
type LifecycleWriteOutcome =
    | Applied of eventId: Guid * businessRevision: int64 * lifecycleSequence: int64
    | Refused of LifecycleRefusal
    | ResourceUnavailable
    | CancelledBeforeAdmission of eventId: Guid
    | Failed of CoreFault
    | Unconfirmed of eventId: Guid

/// Application owns lifecycle decisions, while this port owns primary locks, grant rechecks,
/// exact witness intent, co-commit and settlement. No caller receives a table or witness handle.
type internal ICaseLifecycleStore =
    abstract Review: actor: ActorCallContext * caseReference: string -> Task<LifecycleReviewOutcome>

    abstract Apply: actor: ActorCallContext * change: LifecycleChange -> Task<LifecycleWriteOutcome>

    abstract Approve:
        actor: ActorCallContext *
        change: LifecycleChange *
        approvalId: Guid *
        expiresAt: DateTimeOffset ->
            Task<LifecycleWriteOutcome>
