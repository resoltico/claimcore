namespace ClaimCore.Application

[<RequireQualifiedAccess>]
type FaultCode =
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

[<RequireQualifiedAccess>]
type CoreFault =
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | OperationContentConflict

module CoreFaults =
    let private policies =
        [
            CoreFault.StoreUnavailable,
            "CORE_STORE_UNAVAILABLE",
            FaultCode.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.StoreIntegrityError,
            "CORE_STORE_INTEGRITY_ERROR",
            FaultCode.StoreIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.SchemaMismatch,
            "CORE_SCHEMA_MISMATCH",
            FaultCode.SchemaMismatch,
            RecommendedAction.StopAndInvestigate
            CoreFault.OperationContentConflict,
            "CORE_OPERATION_CONTENT_CONFLICT",
            FaultCode.OperationContentConflict,
            RecommendedAction.StopAndInvestigate
        ]

    let private policy reason =
        policies |> List.find (fun (value, _, _, _) -> value = reason)

    let all = policies |> List.map (fun (reason, id, _, _) -> reason, id)
    let token reason = let _, id, _, _ = policy reason in id

    let internal code reason =
        let _, _, code, _ = policy reason in code

    let internal action reason =
        let _, _, _, action = policy reason in action

    let definitions: DiagnosticDefinition list =
        all |> List.map (fun (_, id) -> { Id = id; Parameters = [] })

type CoreFault with
    member this.Code = CoreFaults.code this
    member this.Action = CoreFaults.action this
