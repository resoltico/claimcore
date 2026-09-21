namespace ClaimCore.Application

type FaultCode =
    | StoreUnavailable
    | StoreIntegrityError
    | SchemaMismatch
    | RecoveryCapacityExceeded
    | RecoveryIntegrityError
    | CommitOutcomeUnknown
    | TechnicalMutationUnknown

/// Closed, language-independent reason. Display text belongs to Contracts.
[<RequireQualifiedAccess>]
type CoreFault =
    | OperationContentConflict
    | StoreUnavailable
    | CommitOutcomeUnknown
    | StoreIntegrityError
    | SchemaMismatch
    | RecoveryResponseInvalid
    | RecoveryContentConflict
    | RecoveryPreparationMissing
    | RecoveryCapacityExhausted
    | RecoverySchemaMismatch
    | RecoveryStoreUnavailable
    | RecoveryStoreIntegrityError
    | RecoveryReadCancelled
    | RecoveryMutationCancelled
    | RecoveryMutationUnknown
    | RetainedCanonicalInvalid
    | RetainedDomainShapeInvalid
    | RetainedPreparationUnverifiable
    | NewPreparationMissing
    | PreparationDismissedBeforeExecution
    | RetainedDigestMismatch
    | StoredRecoveryInvalid

/// One reviewed policy table owns identity and guidance together; presentation is external.
module CoreFaults =
    let private policy0 =
        [
            CoreFault.OperationContentConflict,
            "CORE_OPERATION_CONTENT_CONFLICT",
            FaultCode.TechnicalMutationUnknown,
            RecommendedAction.StopAndInvestigate
            CoreFault.StoreUnavailable,
            "CORE_STORE_UNAVAILABLE",
            FaultCode.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.CommitOutcomeUnknown,
            "CORE_COMMIT_OUTCOME_UNKNOWN",
            FaultCode.CommitOutcomeUnknown,
            RecommendedAction.RecoverExact
            CoreFault.StoreIntegrityError,
            "CORE_STORE_INTEGRITY_ERROR",
            FaultCode.StoreIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.SchemaMismatch,
            "CORE_SCHEMA_MISMATCH",
            FaultCode.SchemaMismatch,
            RecommendedAction.StopAndInvestigate
            CoreFault.RecoveryResponseInvalid,
            "RECOVERY_STORE_RESPONSE_INVALID",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
        ]

    let private policy1 =
        [
            CoreFault.RecoveryContentConflict,
            "RECOVERY_STORE_CONTENT_CONFLICT",
            FaultCode.TechnicalMutationUnknown,
            RecommendedAction.StopAndInvestigate
            CoreFault.RecoveryPreparationMissing,
            "RECOVERY_STORE_PREPARATION_MISSING",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.RecoveryCapacityExhausted,
            "RECOVERY_CAPACITY_EXHAUSTED",
            FaultCode.RecoveryCapacityExceeded,
            RecommendedAction.StopAndInvestigate
            CoreFault.RecoverySchemaMismatch,
            "RECOVERY_SCHEMA_MISMATCH",
            FaultCode.SchemaMismatch,
            RecommendedAction.StopAndInvestigate
            CoreFault.RecoveryStoreUnavailable,
            "RECOVERY_STORE_UNAVAILABLE",
            FaultCode.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.RecoveryStoreIntegrityError,
            "RECOVERY_STORE_INTEGRITY_ERROR",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
        ]

    let private policy2 =
        [
            CoreFault.RecoveryReadCancelled,
            "RECOVERY_READ_CANCELLED",
            FaultCode.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.RecoveryMutationCancelled,
            "RECOVERY_MUTATION_CANCELLED_BEFORE_COMMIT",
            FaultCode.StoreUnavailable,
            RecommendedAction.RetrySafe
            CoreFault.RecoveryMutationUnknown,
            "RECOVERY_MUTATION_OUTCOME_UNKNOWN",
            FaultCode.TechnicalMutationUnknown,
            RecommendedAction.RecoverExact
            CoreFault.RetainedCanonicalInvalid,
            "RECOVERY_RETAINED_CANONICAL_INVALID",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.RetainedDomainShapeInvalid,
            "RECOVERY_RETAINED_DOMAIN_SHAPE_INVALID",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.RetainedPreparationUnverifiable,
            "RECOVERY_RETAINED_PREPARATION_UNVERIFIABLE",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
        ]

    let private policy3 =
        [
            CoreFault.NewPreparationMissing,
            "RECOVERY_NEW_PREPARATION_MISSING",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.PreparationDismissedBeforeExecution,
            "RECOVERY_PREPARATION_DISMISSED_BEFORE_EXECUTION",
            FaultCode.TechnicalMutationUnknown,
            RecommendedAction.RecoverExact
            CoreFault.RetainedDigestMismatch,
            "RECOVERY_RETAINED_DIGEST_MISMATCH",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
            CoreFault.StoredRecoveryInvalid,
            "RECOVERY_STORED_DATA_INVALID",
            FaultCode.RecoveryIntegrityError,
            RecommendedAction.StopAndInvestigate
        ]

    let private policies = policy0 @ policy1 @ policy2 @ policy3

    let all = policies |> List.map (fun (reason, id, _, _) -> reason, id)

    let private policy reason =
        policies |> List.find (fun (value, _, _, _) -> value = reason)

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
