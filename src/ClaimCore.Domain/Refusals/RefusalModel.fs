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
    | ReferenceUnavailable
    | NotOffered of state: CaseHeadline
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | PaymentNotFound
    | PartyLimit
    | SelfLink
    | DuplicateLink
    | LinkLimitReached

[<RequireQualifiedAccess>]
type PeopleRefusal =
    | LastOwner
    | DistinctOwnerRequired
    | ActorAlreadyRegistered
    | ActorUnknown
    | ActorChanged
    | HumanPrincipalRequired
    | RoleScopeNotApplicable
    | IdentityReserved
    | NoChanges

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
    | AttachmentDuplicate of attachmentId: AttachmentId
    | AttachmentCapacityReached
    | AttachmentPreviewUnavailable
    | InvalidCursor
    | TraversalChanged
    | CaseDateRange of upper: CaseDateFilter
    | RegisterMaintenance

module Refusal =
    let revisionConflict revision =
        Refusal.RevisionConflict(RevisionConflictInfo.at revision)

    let invalidInput (target, violation) =
        Refusal.InvalidInput(InputIssues.singleton target violation)

    let inapplicableForOffer =
        function
        | Refusal.Case(CaseRefusal.NotOffered _)
        | Refusal.Reminder(ReminderRuleRefusal.NotOffered _) -> true
        | Refusal.Case(CaseRefusal.ReferenceUnavailable | CaseRefusal.WorkNoChanges | CaseRefusal.AssigneeUnavailable | CaseRefusal.FamilyChanged | CaseRefusal.CorrectionNoChanges | CaseRefusal.CorrectionRequiresExistingValue | CaseRefusal.PaymentNotFound | CaseRefusal.PartyLimit | CaseRefusal.SelfLink | CaseRefusal.DuplicateLink | CaseRefusal.LinkLimitReached)
        | Refusal.Reminder ReminderRuleRefusal.NoChanges
        | Refusal.People(PeopleRefusal.LastOwner | PeopleRefusal.DistinctOwnerRequired | PeopleRefusal.ActorAlreadyRegistered | PeopleRefusal.ActorUnknown | PeopleRefusal.ActorChanged | PeopleRefusal.HumanPrincipalRequired | PeopleRefusal.RoleScopeNotApplicable | PeopleRefusal.IdentityReserved | PeopleRefusal.NoChanges)
        | Refusal.Access(AccessRefusal.ResourceUnavailable | AccessRefusal.NotAuthorized _)
        | Refusal.InvalidInput _
        | Refusal.RevisionConflict _
        | Refusal.OperationIdOutOfWindow _
        | Refusal.OperationClockUnavailable _
        | Refusal.IdempotencyConflict
        | Refusal.AttachmentDuplicate _
        | Refusal.AttachmentCapacityReached
        | Refusal.AttachmentPreviewUnavailable
        | Refusal.InvalidCursor
        | Refusal.TraversalChanged
        | Refusal.CaseDateRange _
        | Refusal.RegisterMaintenance -> false
