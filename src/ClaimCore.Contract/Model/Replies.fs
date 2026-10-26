namespace ClaimCore.Contract

open ClaimCore.Domain

open System

type Session =
    {
        Authenticated: bool
        ContractIdentity: string
        AntiforgeryToken: string option
    }

type AuthorityState =
    {
        Enabled: bool
        Grants: AuthorityGrant list
    }

type AuthorityReview =
    {
        Target: ActorSummary option
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

/// The public host failure carries no server prose or transport status.
type HostFailure =
    {
        Code: string
        Phase: ExecutionPhase option
    }
