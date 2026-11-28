namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | StopAndInvestigate
    | AskForAccess
    | ReviewLatest
    | RefreshPrecondition
    | NoneRequired

module Refusals =
    let private caseAction =
        function
        | CaseRefusal.CorrectionNoChanges
        | CaseRefusal.WorkNoChanges -> RecommendedAction.NoneRequired
        | CaseRefusal.SelfLink
        | CaseRefusal.DuplicateLink
        | CaseRefusal.LinkLimitReached
        | CaseRefusal.CorrectionRequiresExistingValue -> RecommendedAction.CorrectInput
        | CaseRefusal.FamilyChanged -> RecommendedAction.RefreshPrecondition
        | CaseRefusal.PaymentNotFound
        | CaseRefusal.PartyLimit -> RecommendedAction.ReviewLatest
        | CaseRefusal.AssigneeUnavailable
        | CaseRefusal.NotOffered _ -> RecommendedAction.ReviewLatest

    let action =
        function
        | Refusal.InvalidInput _
        | Refusal.CaseDateRange _
        | Refusal.OperationIdOutOfWindow _ -> RecommendedAction.CorrectInput
        | Refusal.Access(AccessRefusal.ResourceUnavailable | AccessRefusal.NotAuthorized _) ->
            RecommendedAction.AskForAccess
        | Refusal.Case value -> caseAction value
        | Refusal.AttachmentDuplicate _
        | Refusal.AttachmentCapacityReached
        | Refusal.RegisterMaintenance
        | Refusal.InvalidCursor
        | Refusal.TraversalChanged -> RecommendedAction.ReadCurrent
        | Refusal.OperationClockUnavailable _
        | Refusal.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Refusal.AttachmentPreviewUnavailable
        | Refusal.Reminder ReminderRuleRefusal.NoChanges
        | Refusal.People PeopleRefusal.NoChanges -> RecommendedAction.NoneRequired
        | Refusal.People(PeopleRefusal.HumanPrincipalRequired | PeopleRefusal.RoleScopeNotApplicable | PeopleRefusal.IdentityReserved) ->
            RecommendedAction.CorrectInput
        | Refusal.People PeopleRefusal.ActorChanged -> RecommendedAction.RefreshPrecondition
        | Refusal.RevisionConflict _
        | Refusal.Reminder(ReminderRuleRefusal.NotOffered _)
        | Refusal.People(PeopleRefusal.LastOwner | PeopleRefusal.DistinctOwnerRequired | PeopleRefusal.ActorAlreadyRegistered | PeopleRefusal.ActorUnknown) ->
            RecommendedAction.ReviewLatest

    let actualRevision =
        function
        | Refusal.RevisionConflict conflict -> Some conflict.ActualRevision
        | _ -> None

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RecommendedAction =
    let vocabulary =
        Vocabulary.ofUnion<RecommendedAction> "action" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

[<AutoOpen>]
module RefusalProperties =
    type Refusal with
        member this.ActualRevision = Refusals.actualRevision this
        member this.Action = Refusals.action this

module FaultAdvice =
    let internal action =
        function
        | CoreFault.StoreUnavailable -> RecommendedAction.RetrySafe
        | CoreFault.InternalError
        | CoreFault.StoreIntegrityError
        | CoreFault.SchemaMismatch
        | CoreFault.OperationContentConflict -> RecommendedAction.StopAndInvestigate


[<AutoOpen>]
module CoreFaultProperties =
    type CoreFault with
        member this.Action = FaultAdvice.action this
