namespace ClaimCore.Contracts

open ClaimCore.Application

/// Default presentation only; wording is excluded from diagnostic identity.
module CoreFaultPresentation =
    let private group0 =
        [
            CoreFault.OperationContentConflict,
            "The operation ID belongs to different request content."
            CoreFault.StoreUnavailable, "The store was unavailable before completion was confirmed."
            CoreFault.CommitOutcomeUnknown, "Commit completion was not confirmed."
            CoreFault.StoreIntegrityError, "Stored data failed integrity validation."
            CoreFault.SchemaMismatch, "The runtime schema or database settings are incompatible."
            CoreFault.RecoveryResponseInvalid,
            "Recovery storage returned an invalid technical response."
        ]

    let private group1 =
        [
            CoreFault.RecoveryContentConflict, "Recovery identity conflicts with retained data."
            CoreFault.RecoveryPreparationMissing, "Recovery storage lost an expected preparation."
            CoreFault.RecoveryCapacityExhausted, "Recovery capacity is exhausted."
            CoreFault.RecoverySchemaMismatch, "Recovery storage is incompatible with this runtime."
            CoreFault.RecoveryStoreUnavailable,
            "Recovery storage was unavailable before completion was confirmed."
            CoreFault.RecoveryStoreIntegrityError, "Recovery storage failed integrity validation."
        ]

    let private group2 =
        [
            CoreFault.RecoveryReadCancelled, "The recovery read was cancelled."
            CoreFault.RecoveryMutationCancelled,
            "The technical mutation was cancelled before commit."
            CoreFault.RecoveryMutationUnknown,
            "The recovery-state mutation may have committed but could not be confirmed."
            CoreFault.RetainedCanonicalInvalid,
            "Retained canonical request bytes failed integrity validation."
            CoreFault.RetainedDomainShapeInvalid,
            "Retained request did not satisfy its closed domain shape."
            CoreFault.RetainedPreparationUnverifiable,
            "An exact retained preparation could not be verified."
        ]

    let private group3 =
        [
            CoreFault.NewPreparationMissing,
            "A newly retained preparation was not available for execution."
            CoreFault.PreparationDismissedBeforeExecution,
            "The preparation was dismissed before execution could begin."
            CoreFault.RetainedDigestMismatch,
            "The retained preparation digest does not match the submitted draft."
            CoreFault.StoredRecoveryInvalid, "Stored recovery data failed validation."
        ]

    let private explanations = group0 @ group1 @ group2 @ group3

    let render reason =
        explanations |> List.find (fst >> (=) reason) |> snd
