module ClaimCore.Tests.FaultDiagnosticExamples

open ClaimCore.Application

// Explicit reviewed vocabulary and guidance: no reflection-derived identifiers.
let private group0 =
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

let private group1 =
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

let private group2 =
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

let private group3 =
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

let all = group0 @ group1 @ group2 @ group3
