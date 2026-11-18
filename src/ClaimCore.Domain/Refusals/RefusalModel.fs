namespace ClaimCore.Domain

open System

/// Collection identity and bounded ordinals stay independent of transport and UI-local row keys.
[<RequireQualifiedAccess>]
type TaskCollectionId =
    | PeopleGrants
    | Parties

    member this.MaximumRows =
        match this with
        | TaskCollectionId.PeopleGrants -> PeopleAdmission.MaximumGrants
        | TaskCollectionId.Parties -> PartyContent.MaximumCurrent

type TaskRowTarget =
    private
        {
            Collection: TaskCollectionId
            Ordinal: int
        }

module TaskRowTarget =
    let create (collection: TaskCollectionId) ordinal =
        if ordinal < 0 || ordinal >= collection.MaximumRows then
            None
        else
            Some
                {
                    Collection = collection
                    Ordinal = ordinal
                }

    let collection target = target.Collection
    let ordinal target = target.Ordinal

type InputIssue =
    {
        Target: InputTarget
        Violation: Violation
        Row: TaskRowTarget option
    }

type InputIssues = private InputIssues of InputIssue * InputIssue list

module InputIssues =
    let singleton target violation =
        InputIssues(
            {
                Target = target
                Row = None
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

    let atRow row issues =
        toList issues
        |> List.map (fun issue -> { issue with Row = Some row })
        |> ofList
        |> Option.get

[<RequireQualifiedAccess>]
type CaseRefusal =
    | WorkNoChanges
    | AssigneeUnavailable
    | FamilyChanged
    | CaseAlreadyExists
    | NotOffered of state: CaseHeadline
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | PaymentNotFound
    | PartyLimit

[<RequireQualifiedAccess>]
type PeopleRefusal =
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | ActorChanged
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
