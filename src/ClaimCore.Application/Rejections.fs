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
    | ResourceUnavailable
    | CaseVoided
    | CaseErased
    | InvalidReason
    | AlreadyVoided
    | NotVoided
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired

/// Ordinary refusal: never a store fault, cancellation, or unknown commit outcome.
/// Every property below is derived from this closed reason; no text is accepted from a caller.
[<RequireQualifiedAccess>]
type Rejection =
    | Domain of DomainError
    | PageLimitOutOfRange of maximumPageSize: int
    | InvalidCaseListCursor
    | InvalidHistoryCursor
    | IdempotencyConflict
    | ResourceUnavailable
    | CaseVoided
    | CaseErased
    | InvalidReason
    | AlreadyVoided
    | NotVoided
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired

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

    let internal simplePolicies =
        [
            Rejection.IdempotencyConflict,
            RejectionCode.IdempotencyConflict,
            RejectionDiagnosticId.IdempotencyConflict
            Rejection.ResourceUnavailable,
            RejectionCode.ResourceUnavailable,
            RejectionDiagnosticId.ResourceUnavailable
            Rejection.CaseVoided, RejectionCode.CaseVoided, RejectionDiagnosticId.CaseVoided
            Rejection.CaseErased, RejectionCode.CaseErased, RejectionDiagnosticId.CaseErased
            Rejection.InvalidReason,
            RejectionCode.InvalidReason,
            RejectionDiagnosticId.InvalidReason
            Rejection.AlreadyVoided,
            RejectionCode.AlreadyVoided,
            RejectionDiagnosticId.AlreadyVoided
            Rejection.NotVoided, RejectionCode.NotVoided, RejectionDiagnosticId.NotVoided
            Rejection.LastOwner, RejectionCode.LastOwner, RejectionDiagnosticId.LastOwner
            Rejection.ActorAlreadyRegistered,
            RejectionCode.ActorAlreadyRegistered,
            RejectionDiagnosticId.ActorAlreadyRegistered
            Rejection.ActorUnknown, RejectionCode.ActorUnknown, RejectionDiagnosticId.ActorUnknown
            Rejection.HumanPrincipalRequired,
            RejectionCode.HumanPrincipalRequired,
            RejectionDiagnosticId.HumanPrincipalRequired
        ]

    let code =
        function
        | Rejection.Domain error -> domainCode error
        | Rejection.PageLimitOutOfRange _
        | Rejection.InvalidCaseListCursor
        | Rejection.InvalidHistoryCursor -> RejectionCode.InvalidInput
        | reason ->
            simplePolicies
            |> List.find (fun (value, _, _) -> value = reason)
            |> fun (_, code, _) -> code

    let field =
        function
        | Rejection.Domain(DomainError.InvalidInput(target, _)) -> Some(InputTargets.token target)
        | Rejection.PageLimitOutOfRange _ -> Some "limit"
        | Rejection.InvalidCaseListCursor
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
        | Rejection.InvalidCaseListCursor
        | Rejection.InvalidHistoryCursor
        | Rejection.InvalidReason -> RecommendedAction.CorrectInput
        | Rejection.Domain(DomainError.VersionConflict _) -> RecommendedAction.ReadCurrent
        | Rejection.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | _ -> RecommendedAction.NoneRequired

type Rejection with
    member this.Code = Rejections.code this
    member this.Field = Rejections.field this
    member this.ActualVersion = Rejections.actualVersion this
    member this.Action = Rejections.action this
