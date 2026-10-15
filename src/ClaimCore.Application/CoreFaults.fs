namespace ClaimCore.Application

[<RequireQualifiedAccess>]
type CoreFault =
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

module CoreFaults =
    let private policies =
        [
            CoreFault.StoreUnavailable, "CORE_STORE_UNAVAILABLE", RecommendedAction.RetrySafe
            CoreFault.StoreIntegrityError,
            "CORE_STORE_INTEGRITY_ERROR",
            RecommendedAction.StopAndInvestigate
            CoreFault.SchemaMismatch, "CORE_SCHEMA_MISMATCH", RecommendedAction.StopAndInvestigate
            CoreFault.OperationContentConflict,
            "CORE_OPERATION_CONTENT_CONFLICT",
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
