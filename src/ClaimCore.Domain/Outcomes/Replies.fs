namespace ClaimCore.Domain


open System

type AuthorityState =
    {
        Enabled: bool
        Grants: AuthoredGrant list
    }

type AuthorityReview =
    {
        Target: Actor option
        Before: AuthorityState
        After: AuthorityState
    }

[<RequireQualifiedAccess>]
type OperationReview =
    | Case of AdvisoryReview
    | Authority of AuthorityReview

[<RequireQualifiedAccess>]
type ReadReply<'T> =
    | Succeeded of 'T
    | Rejected of Refusal
    | Failed of CoreFault

[<RequireQualifiedAccess>]
type PreviewResult =
    | Reviewed of OperationReview
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault

type PreviewReply =
    {
        OperationId: Guid
        Outcome: PreviewResult
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
        OperationId: Guid
        Outcome: ExecuteResult
    }
