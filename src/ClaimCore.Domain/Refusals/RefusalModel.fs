namespace ClaimCore.Domain

open System

type RelatedDate =
    {
        Field: BusinessField
        Value: CalendarDate
    }

type InputIssue =
    {
        Target: InputTarget
        Violation: Violation
        RelatedDate: RelatedDate option
    }

type InputIssues = private InputIssues of InputIssue * InputIssue list

module InputIssues =
    let singleton target violation =
        InputIssues(
            {
                Target = target
                RelatedDate = None
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
    | WorkNoChanges
    | AssigneeUnavailable
    | CaseAlreadyExists
    | NotOffered of state: CaseHeadline
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | DecisionHasPayments
    | PaymentNotFound
    | PaymentAlreadyRecorded

[<RequireQualifiedAccess>]
type AuthorityRefusal =
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired
    | RoleScopeNotApplicable

[<RequireQualifiedAccess>]
type AccessRefusal =
    | ResourceUnavailable
    | NotAuthorized of required: Capability

[<RequireQualifiedAccess>]
type Refusal =
    | InvalidInput of InputIssues
    | RevisionConflict of RevisionConflictInfo
    | Case of CaseRefusal
    | Reminder of ReminderRuleRefusal
    | Authority of AuthorityRefusal
    | Access of AccessRefusal
    | OperationIdOutOfWindow of issuedAt: DateTimeOffset * observedAt: DateTimeOffset
    | IdempotencyConflict
    | InvalidCursor
    | RegisterMaintenance

module Refusal =
    let revisionConflict revision =
        Refusal.RevisionConflict(RevisionConflictInfo.at revision)

    let invalidInput (target, violation) =
        Refusal.InvalidInput(InputIssues.singleton target violation)
