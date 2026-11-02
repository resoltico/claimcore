namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

type AuthorityState =
    {
        Enabled: bool
        Grants: ScopedGrant list
        Access: CapabilityAccess list
    }

type AuthorityReview =
    {
        Target: Actor option
        AccessScope: GrantScope
        Before: AuthorityState
        After: AuthorityState
    }

type WorkReview =
    {
        Reference: CaseReference
        Before: CaseWorkSnapshot
        Proposed: CaseWorkSnapshot
        Context: RuntimeContext
    }

type ReminderReview =
    {
        Reference: CaseReference
        CaseId: CaseId
        Before: Reminder option
        Effects: ReminderEffects
        Context: RuntimeContext
    }

[<RequireQualifiedAccess>]
type OperationReview =
    | Case of AdvisoryReview
    | Authority of AuthorityReview
    | Work of WorkReview
    | Reminder of ReminderReview

[<RequireQualifiedAccess>]
type Previewed =
    | Reviewed of OperationReview
    | AlreadyAccepted of OperationReceipt

type PreviewReply =
    {
        // Correlation echoes authored input even when admission refuses its identity.
        OperationId: Guid
        Outcome: Outcome<Previewed>
    }

[<RequireQualifiedAccess>]
type ExecuteResult =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | OutcomeUnknown

type ExecuteReply =
    {
        // Correlation echoes authored input even when admission refuses its identity.
        OperationId: Guid
        Outcome: ExecuteResult
    }

type OperationReply<'success> =
    {
        OperationId: Guid
        Outcome: Outcome<'success>
    }
