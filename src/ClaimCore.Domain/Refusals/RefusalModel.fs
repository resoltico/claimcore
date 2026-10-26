namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type Refusal =
    | InvalidInput of InputTarget * Violation
    | RevisionConflict of actualRevision: int64
    | CaseAlreadyExists
    | CaseErased
    | CaseVoided
    | AlreadyVoided
    | NotVoided
    | CaseClosed
    | AlreadyClosed
    | AlreadyOpened
    | AmendmentRequiresUndecided
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | DecisionRequired
    | PaymentAlreadyRecorded
    | PaymentNotRecorded
    | DecisionAlreadyPaid
    | ZeroDecisionCannotBePaid
    | OperationResolved
    | IdempotencyConflict
    | ResourceUnavailable
    | ActNotPermitted
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired
    | InvalidCursor

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | ReplayExact
    | Reauthenticate
    | StopAndInvestigate
    | NoneRequired

type RefusalRow =
    {
        Code: string
        Action: RecommendedAction
    }
