namespace ClaimCore.Protocol

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
        | CaseRefusal.AlreadyVoided
        | CaseRefusal.AlreadyClosed
        | CaseRefusal.AlreadyOpened
        | CaseRefusal.CorrectionNoChanges
        | CaseRefusal.WorkNoChanges
        | CaseRefusal.CaseErased -> RecommendedAction.NoneRequired
        | _ -> RecommendedAction.ReviewLatest

    let action =
        function
        | Refusal.InvalidInput _ -> RecommendedAction.CorrectInput
        | Refusal.Access _ -> RecommendedAction.AskForAccess
        | Refusal.Case value -> caseAction value
        | Refusal.OperationResolved
        | Refusal.RegisterMaintenance
        | Refusal.InvalidCursor -> RecommendedAction.ReadCurrent
        | Refusal.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Refusal.Reminder ReminderRuleRefusal.NoChanges -> RecommendedAction.NoneRequired
        | Refusal.RevisionConflict _
        | Refusal.Reminder _
        | Refusal.Authority _ -> RecommendedAction.ReviewLatest

    let actualRevision =
        function
        | Refusal.RevisionConflict revision -> Some revision
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
