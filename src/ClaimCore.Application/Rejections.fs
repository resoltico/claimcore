namespace ClaimCore.Application

open ClaimCore.Domain

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | RecoverExact
    | Reauthenticate
    | StopAndInvestigate
    | NoneRequired

type RejectionCode =
    | InvalidInput
    | CaseNotFound
    | CaseAlreadyExists
    | VersionConflict
    | CaseClosed
    | AmendmentRequiresUndecided
    | DecisionRequired
    | PaymentAlreadyRecorded
    | PaymentNotRecorded
    | DecisionAlreadyPaid
    | AlreadyClosed
    | AlreadyOpened
    | ZeroDecisionCannotBePaid
    | IdempotencyConflict
    | OperationRevoked
    | RecoveryAttemptLimitReached

/// Ordinary refusal: never a store fault, cancellation, or unknown commit outcome.
/// Every property below is derived from this closed reason; no text is accepted from a caller.
[<RequireQualifiedAccess>]
type Rejection =
    | Domain of DomainError
    | PageLimitOutOfRange of maximumPageSize: int
    | InvalidHistoryCursor
    | IdempotencyConflict
    | OperationRevoked
    | RecoveryAttemptLimitReached

module Rejections =
    let private progressCode =
        function
        | DomainError.AmendmentRequiresUndecided -> RejectionCode.AmendmentRequiresUndecided
        | DomainError.CorrectionNoChanges
        | DomainError.CorrectionRequiresExistingValue -> RejectionCode.InvalidInput
        | DomainError.DecisionRequired -> RejectionCode.DecisionRequired
        | DomainError.PaymentAlreadyRecorded -> RejectionCode.PaymentAlreadyRecorded
        | DomainError.PaymentNotRecorded -> RejectionCode.PaymentNotRecorded
        | DomainError.DecisionAlreadyPaid -> RejectionCode.DecisionAlreadyPaid
        | DomainError.ZeroDecisionCannotBePaid -> RejectionCode.ZeroDecisionCannotBePaid
        | _ -> invalidArg "error" "Expected a progress refusal."

    let private domainCode =
        function
        | DomainError.InvalidInput _ -> RejectionCode.InvalidInput
        | DomainError.NotFound -> RejectionCode.CaseNotFound
        | DomainError.AlreadyExists -> RejectionCode.CaseAlreadyExists
        | DomainError.VersionConflict _ -> RejectionCode.VersionConflict
        | DomainError.ClosedCase -> RejectionCode.CaseClosed
        | DomainError.AlreadyClosed -> RejectionCode.AlreadyClosed
        | DomainError.AlreadyOpened -> RejectionCode.AlreadyOpened
        | progress -> progressCode progress

    let code =
        function
        | Rejection.Domain error -> domainCode error
        | Rejection.PageLimitOutOfRange _
        | Rejection.InvalidHistoryCursor -> RejectionCode.InvalidInput
        | Rejection.IdempotencyConflict -> RejectionCode.IdempotencyConflict
        | Rejection.OperationRevoked -> RejectionCode.OperationRevoked
        | Rejection.RecoveryAttemptLimitReached -> RejectionCode.RecoveryAttemptLimitReached

    let field =
        function
        | Rejection.Domain(DomainError.InvalidInput(target, _)) -> Some(InputTargets.token target)
        | Rejection.PageLimitOutOfRange _ -> Some "limit"
        | Rejection.InvalidHistoryCursor -> Some "cursor"
        | _ -> None

    let actualVersion =
        function
        | Rejection.Domain(DomainError.VersionConflict value) -> Some value
        | _ -> None

    let action =
        function
        | Rejection.Domain(DomainError.InvalidInput _)
        | Rejection.PageLimitOutOfRange _
        | Rejection.InvalidHistoryCursor -> RecommendedAction.CorrectInput
        | Rejection.Domain(DomainError.VersionConflict _)
        | Rejection.OperationRevoked
        | Rejection.RecoveryAttemptLimitReached -> RecommendedAction.ReadCurrent
        | Rejection.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Rejection.Domain _ -> RecommendedAction.NoneRequired

type Rejection with
    member this.Code = Rejections.code this
    member this.Field = Rejections.field this
    member this.ActualVersion = Rejections.actualVersion this
    member this.Action = Rejections.action this
