namespace ClaimCore.Application

open ClaimCore.Catalog

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

    let all = policies |> List.map (fun (reason, id, _) -> reason, id)
    let token reason = let _, id, _ = policy reason in id

    let internal action reason =
        let _, _, action = policy reason in action

    let definitions: DiagnosticDefinition list =
        all |> List.map (fun (_, id) -> { Id = id; Parameters = [] })

type CoreFault with
    member this.Action = CoreFaults.action this
