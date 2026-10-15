namespace ClaimCore.Application

open ClaimCore.Domain

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | ReplayExact
    | Reauthenticate
    | StopAndInvestigate
    | NoneRequired

/// Every refusal is a closed reason; adapters derive diagnostics and never accept caller messages.
[<RequireQualifiedAccess>]
type Rejection =
    | Case of CaseRefusal
    | PageLimitOutOfRange of maximumPageSize: int
    | InvalidCursor
    | IdempotencyConflict
    | ResourceUnavailable
    | ActNotPermitted
    | LedgerExhausted
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired

module Rejections =
    let internal simplePolicies =
        [
            Rejection.IdempotencyConflict, RejectionDiagnosticId.IdempotencyConflict
            Rejection.ResourceUnavailable, RejectionDiagnosticId.ResourceUnavailable
            Rejection.ActNotPermitted, RejectionDiagnosticId.ActNotPermitted
            Rejection.LedgerExhausted, RejectionDiagnosticId.LedgerExhausted
            Rejection.InvalidCursor, RejectionDiagnosticId.InvalidCursor
            Rejection.LastOwner, RejectionDiagnosticId.LastOwner
            Rejection.ActorAlreadyRegistered, RejectionDiagnosticId.ActorAlreadyRegistered
            Rejection.ActorUnknown, RejectionDiagnosticId.ActorUnknown
            Rejection.HumanPrincipalRequired, RejectionDiagnosticId.HumanPrincipalRequired
        ]

    let field =
        function
        | Rejection.Case(CaseRefusal.InvalidInput(target, _)) -> Some(InputTargets.token target)
        | Rejection.PageLimitOutOfRange _ -> Some "limit"
        | Rejection.InvalidCursor -> Some "cursor"
        | _ -> None

    let actualRevision =
        function
        | Rejection.Case(CaseRefusal.RevisionConflict value) -> Some value
        | _ -> None

    let action =
        function
        | Rejection.Case(CaseRefusal.InvalidInput _)
        | Rejection.PageLimitOutOfRange _
        | Rejection.InvalidCursor -> RecommendedAction.CorrectInput
        | Rejection.Case(CaseRefusal.RevisionConflict _) -> RecommendedAction.ReadCurrent
        | Rejection.IdempotencyConflict
        | Rejection.LedgerExhausted -> RecommendedAction.StopAndInvestigate
        | _ -> RecommendedAction.NoneRequired

type Rejection with
    member this.Field = Rejections.field this
    member this.ActualRevision = Rejections.actualRevision this
    member this.Action = Rejections.action this
