namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | StopAndInvestigate
    | ChooseAnotherReference
    | AskForAccess
    | ReviewLatest
    | NoneRequired

module Refusals =
    let private caseAction =
        function
        | CaseRefusal.CaseAlreadyExists -> RecommendedAction.ChooseAnotherReference
        | CaseRefusal.CorrectionNoChanges
        | CaseRefusal.WorkNoChanges -> RecommendedAction.NoneRequired
        | CaseRefusal.CorrectionRequiresExistingValue
        | CaseRefusal.DecisionHasPayments -> RecommendedAction.CorrectInput
        | CaseRefusal.PaymentNotFound -> RecommendedAction.ReviewLatest
        | CaseRefusal.AssigneeUnavailable
        | CaseRefusal.NotOffered _ -> RecommendedAction.ReviewLatest

    let action =
        function
        | Refusal.InvalidInput _
        | Refusal.OperationIdOutOfWindow _ -> RecommendedAction.CorrectInput
        | Refusal.Access(AccessRefusal.ResourceUnavailable | AccessRefusal.NotAuthorized _) ->
            RecommendedAction.AskForAccess
        | Refusal.Case value -> caseAction value
        | Refusal.RegisterMaintenance
        | Refusal.InvalidCursor -> RecommendedAction.ReadCurrent
        | Refusal.OperationClockUnavailable _
        | Refusal.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Refusal.Reminder ReminderRuleRefusal.NoChanges -> RecommendedAction.NoneRequired
        | Refusal.People(PeopleRefusal.HumanPrincipalRequired | PeopleRefusal.RoleScopeNotApplicable) ->
            RecommendedAction.CorrectInput
        | Refusal.RevisionConflict _
        | Refusal.Reminder(ReminderRuleRefusal.NotOffered _)
        | Refusal.People(PeopleRefusal.LastOwner | PeopleRefusal.ActorAlreadyRegistered | PeopleRefusal.ActorUnknown) ->
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
