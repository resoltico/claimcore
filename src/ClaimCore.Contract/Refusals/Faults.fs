namespace ClaimCore.Contract

open ClaimCore.Domain


[<RequireQualifiedAccess>]
type CoreFault =
    | InternalError
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

module CoreFaults =
    let private policy =
        function
        | CoreFault.InternalError ->
            FaultDiagnosticIds.InternalError, RecommendedAction.StopAndInvestigate
        | CoreFault.StoreUnavailable ->
            FaultDiagnosticIds.StoreUnavailable, RecommendedAction.RetrySafe
        | CoreFault.StoreIntegrityError ->
            FaultDiagnosticIds.StoreIntegrityError, RecommendedAction.StopAndInvestigate
        | CoreFault.SchemaMismatch ->
            FaultDiagnosticIds.SchemaMismatch, RecommendedAction.StopAndInvestigate
        | CoreFault.OperationContentConflict ->
            FaultDiagnosticIds.OperationContentConflict, RecommendedAction.StopAndInvestigate

    let vocabulary =
        Vocabulary.ofUnion<CoreFault> "fault" TokenCasing.ScreamingSnake (Some "CORE_")

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary
    let internal action reason = policy reason |> snd

    let definitions: DiagnosticDefinition list =
        all |> List.map (fun (_, id) -> { Id = id; Parameters = [] })

type CoreFault with
    member this.Action = CoreFaults.action this
