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
type CaseRefusal =
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

[<RequireQualifiedAccess>]
type AuthorityRefusal =
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired

[<RequireQualifiedAccess>]
type AccessRefusal =
    | ResourceUnavailable
    | ActNotPermitted

[<RequireQualifiedAccess>]
type Refusal =
    | InvalidInput of InputIssues
    | RevisionConflict of actualRevision: Revision
    | Case of CaseRefusal
    | Authority of AuthorityRefusal
    | Access of AccessRefusal
    | OperationResolved
    | IdempotencyConflict
    | InvalidCursor

module Refusal =
    let invalidInput (target, violation) =
        Refusal.InvalidInput(InputIssues.singleton target violation)
