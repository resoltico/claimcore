namespace ClaimCore.Contract


[<RequireQualifiedAccess>]
type CoreFault =
    | InternalError
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

module CoreFaults =
    let private policies =
        [
            CoreFault.InternalError,
            FaultDiagnosticIds.InternalError,
            RecommendedAction.StopAndInvestigate
            CoreFault.StoreUnavailable,
            FaultDiagnosticIds.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.StoreIntegrityError,
            FaultDiagnosticIds.StoreIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.SchemaMismatch,
            FaultDiagnosticIds.SchemaMismatch,
            RecommendedAction.StopAndInvestigate
            CoreFault.OperationContentConflict,
            FaultDiagnosticIds.OperationContentConflict,
            RecommendedAction.StopAndInvestigate
        ]

    let private policy reason =
        policies |> List.find (fun (value, _, _) -> value = reason)

    let vocabulary =
        policies
        |> List.map (fun (reason, id, _) -> reason, id)
        |> Vocabulary.define "fault"

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

    let internal action reason =
        let _, _, action = policy reason in action

    let definitions: DiagnosticDefinition list =
        all |> List.map (fun (_, id) -> { Id = id; Parameters = [] })

type CoreFault with
    member this.Action = CoreFaults.action this
