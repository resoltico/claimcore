namespace ClaimCore.Protocol

open ClaimCore.Domain

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | StopAndInvestigate
    | NoneRequired

module Refusals =
    let action =
        function
        | Refusal.InvalidInput _ -> RecommendedAction.CorrectInput
        | Refusal.RevisionConflict _
        | Refusal.OperationResolved
        | Refusal.InvalidCursor -> RecommendedAction.ReadCurrent
        | Refusal.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | Refusal.Case _
        | Refusal.Authority _
        | Refusal.Access _ -> RecommendedAction.NoneRequired

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
