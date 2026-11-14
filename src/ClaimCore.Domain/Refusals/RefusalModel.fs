namespace ClaimCore.Domain

open System

type RelatedDate =
    {
        Field: BusinessField
        Value: CalendarDate
    }

/// Ordinal in the submitted people grant collection, independent of client row identity.
[<Struct>]
type GrantOrdinal = private GrantOrdinal of int

module GrantOrdinal =
    let create value =
        if value < 0 || value >= PeopleAdmission.MaximumGrants then
            None
        else
            Some(GrantOrdinal value)

    let value (GrantOrdinal value) = value

type InputIssue =
    {
        Target: InputTarget
        Violation: Violation
        RelatedDate: RelatedDate option
        GrantRow: GrantOrdinal option
    }

type InputIssues = private InputIssues of InputIssue * InputIssue list

module InputIssues =
    let singleton target violation =
        InputIssues(
            {
                Target = target
                GrantRow = None
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

    let atGrantRow ordinal issues =
        toList issues
        |> List.map (fun issue -> { issue with GrantRow = Some ordinal })
        |> ofList
        |> Option.get

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

[<RequireQualifiedAccess>]
type PeopleRefusal =
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
    | People of PeopleRefusal
    | Access of AccessRefusal
    | OperationIdOutOfWindow of issuedAt: DateTimeOffset * observedAt: DateTimeOffset
    | OperationClockUnavailable of expiredBefore: DateTimeOffset * observedAt: DateTimeOffset
    | IdempotencyConflict
    | InvalidCursor
    | RegisterMaintenance

module Refusal =
    let revisionConflict revision =
        Refusal.RevisionConflict(RevisionConflictInfo.at revision)

    let invalidInput (target, violation) =
        Refusal.InvalidInput(InputIssues.singleton target violation)
