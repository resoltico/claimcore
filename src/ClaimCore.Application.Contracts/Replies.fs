namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

type PeopleState =
    {
        Profile: ActorProfile option
        Enabled: bool
        Grants: ScopedGrant list
        Access: CapabilityAccess list
    }

[<RequireQualifiedAccess>]
type PeopleReviewEffect =
    | Register of PrincipalKey
    | ProvisionOwner of PrincipalKey
    | SetProfile of ActorProfile
    | EraseProfile
    | Grant of ScopedGrant
    | Revoke of ScopedGrant
    | Enable
    | Disable

type PeopleReview =
    {
        Basis: WorkDateBasis
        Effects: PeopleReviewEffect list
        Target: ActorReviewItem option
        AccessScope: GrantScope
        Before: PeopleState
        After: PeopleState
    }

type WorkReview =
    {
        Parent: CaseSnapshot
        Reference: CaseReference
        Before: CaseWorkReadModel
        Proposed: CaseWorkReadModel
        Context: RuntimeContext
    }

type ReminderReview =
    {
        Parent: CaseSnapshot
        Reference: CaseReference
        CaseId: CaseId
        Before: Reminder option
        Effects: ReminderEffects
        Context: RuntimeContext
    }

type NoteReview =
    {
        Parent: CaseSnapshot
        Reference: CaseReference
        Before: Note option
        Proposed: Note
        Act: NoteActKind
        Context: RuntimeContext
    }

[<RequireQualifiedAccess>]
type OperationReview =
    | Case of AdvisoryReview
    | People of PeopleReview
    | Work of WorkReview
    | Reminder of ReminderReview
    | Note of NoteReview

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
