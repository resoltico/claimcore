namespace ClaimCore.Application.Contracts

open ClaimCore.Domain
open ClaimCore.Domain.Tasks

/// DATA reports task identities. A current server snapshot additionally supplies executable controls.
[<RequireQualifiedAccess>]
type TaskOffer =
    | Declared of TaskId
    | Available of TaskId * Binding * Availability

module TaskOffer =
    let id =
        function
        | TaskOffer.Declared id
        | TaskOffer.Available(id, _, _) -> id


    let private inapplicable =
        function
        | Refusal.Case(CaseRefusal.CaseAlreadyExists | CaseRefusal.NotOffered _)
        | Refusal.Reminder(ReminderRuleRefusal.NotOffered _) -> true
        | Refusal.Case(CaseRefusal.WorkNoChanges | CaseRefusal.AssigneeUnavailable | CaseRefusal.FamilyChanged | CaseRefusal.CorrectionNoChanges | CaseRefusal.CorrectionRequiresExistingValue | CaseRefusal.PaymentNotFound | CaseRefusal.PartyLimit | CaseRefusal.SelfLink | CaseRefusal.DuplicateLink | CaseRefusal.LinkLimitReached)
        | Refusal.Reminder ReminderRuleRefusal.NoChanges
        | Refusal.People(PeopleRefusal.LastOwner | PeopleRefusal.ActorAlreadyRegistered | PeopleRefusal.ActorUnknown | PeopleRefusal.ActorChanged | PeopleRefusal.HumanPrincipalRequired | PeopleRefusal.RoleScopeNotApplicable | PeopleRefusal.IdentityReserved | PeopleRefusal.NoChanges)
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
        | Refusal.CaseDateRange _
        | Refusal.RegisterMaintenance -> false

    let forBinding task binding availability =
        if availability.Blocked |> Option.exists inapplicable then
            TaskOffer.Declared task
        else
            TaskOffer.Available(task, binding, availability)
