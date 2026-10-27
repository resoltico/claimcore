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
type Previewed =
    | Reviewed of OperationReview
    | AlreadyAccepted of OperationReceipt

type PreviewReply =
    {
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
        OperationId: Guid
        Outcome: ExecuteResult
    }
