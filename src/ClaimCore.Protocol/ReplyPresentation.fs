namespace ClaimCore.Protocol

open ClaimCore.Domain

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | ReplayExact
    | Reauthenticate
    | StopAndInvestigate
    | NoneRequired

module Refusals =
    // This exhaustive business policy intentionally requires a decision for every refusal case.
    // fsharplint:disable-next-line CyclomaticComplexity
    let action =
        function
        | Refusal.InvalidInput _ -> RecommendedAction.CorrectInput
        | Refusal.RevisionConflict _
        | Refusal.OperationResolved
        | Refusal.InvalidCursor -> RecommendedAction.ReadCurrent
        | Refusal.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Refusal.CaseAlreadyExists
        | Refusal.CaseErased
        | Refusal.CaseVoided
        | Refusal.AlreadyVoided
        | Refusal.NotVoided
        | Refusal.CaseClosed
        | Refusal.AlreadyClosed
        | Refusal.AlreadyOpened
        | Refusal.AmendmentRequiresUndecided
        | Refusal.CorrectionNoChanges
        | Refusal.CorrectionRequiresExistingValue
        | Refusal.DecisionRequired
        | Refusal.PaymentAlreadyRecorded
        | Refusal.PaymentNotRecorded
        | Refusal.DecisionAlreadyPaid
        | Refusal.ZeroDecisionCannotBePaid
        | Refusal.ResourceUnavailable
        | Refusal.ActNotPermitted
        | Refusal.LastOwner
        | Refusal.ActorAlreadyRegistered
        | Refusal.ActorUnknown
        | Refusal.HumanPrincipalRequired -> RecommendedAction.NoneRequired

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
