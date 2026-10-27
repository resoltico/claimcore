namespace ClaimCore.Domain

type InputIssue =
    {
        Target: InputTarget
        Violation: Violation
    }

type InputIssues = private InputIssues of InputIssue * InputIssue list

module InputIssues =
    let singleton target violation =
        InputIssues(
            {
                Target = target
                Violation = violation
            },
            []
        )

    let toList (InputIssues(first, rest)) = first :: rest
    let head (InputIssues(first, _)) = first

    let ofList =
        function
        | [] -> None
        | first :: rest -> Some(InputIssues(first, rest))

    let append (InputIssues(first, rest)) last = InputIssues(first, rest @ toList last)

[<RequireQualifiedAccess>]
type Refusal =
    | InvalidInput of InputIssues
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

module Refusal =
    let invalidInput (target, violation) =
        Refusal.InvalidInput(InputIssues.singleton target violation)
