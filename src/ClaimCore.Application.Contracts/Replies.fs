namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

/// Reviews describe accepted part changes, independently of task-specific command representations.
type OperationReview =
    {
        Task: TaskId
        Target: ReviewTarget
        References: ChangeReferences
        Binding: Binding
        Changes: ClaimCore.Domain.Changes.DescribedPart list
        Context: RuntimeContext
    }

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
