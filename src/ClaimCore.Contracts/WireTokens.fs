namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

/// One transport-token authority shared by schemas and the pure CLI/Web codecs.
[<RequireQualifiedAccess>]
module WireTokens =
    let command = CommandKinds.token
    let status = CaseStatuses.token

    let action =
        function
        | RecommendedAction.CorrectInput -> "CORRECT_INPUT"
        | RecommendedAction.ReadCurrent -> "READ_CURRENT"
        | RecommendedAction.RetrySafe -> "RETRY_SAFE"
        | RecommendedAction.RecoverExact -> "RECOVER_EXACT"
        | RecommendedAction.Reauthenticate -> "REAUTHENTICATE"
        | RecommendedAction.StopAndInvestigate -> "STOP_AND_INVESTIGATE"
        | RecommendedAction.NoneRequired -> "NONE_REQUIRED"

    let private rejectionCodeMap =
        Map.ofList
            [
                RejectionCode.InvalidInput, "INVALID_INPUT"
                RejectionCode.CaseNotFound, "CASE_NOT_FOUND"
                RejectionCode.CaseAlreadyExists, "CASE_ALREADY_EXISTS"
                RejectionCode.VersionConflict, "VERSION_CONFLICT"
                RejectionCode.CaseClosed, "CASE_CLOSED"
                RejectionCode.AmendmentRequiresUndecided, "AMENDMENT_REQUIRES_UNDECIDED"
                RejectionCode.DecisionRequired, "DECISION_REQUIRED"
                RejectionCode.PaymentAlreadyRecorded, "PAYMENT_ALREADY_RECORDED"
                RejectionCode.PaymentNotRecorded, "PAYMENT_NOT_RECORDED"
                RejectionCode.DecisionAlreadyPaid, "DECISION_ALREADY_PAID"
                RejectionCode.AlreadyClosed, "ALREADY_CLOSED"
                RejectionCode.AlreadyOpened, "ALREADY_OPENED"
                RejectionCode.ZeroDecisionCannotBePaid, "ZERO_DECISION_CANNOT_BE_PAID"
                RejectionCode.IdempotencyConflict, "IDEMPOTENCY_CONFLICT"
                RejectionCode.OperationRevoked, "OPERATION_REVOKED"
                RejectionCode.RecoveryAttemptLimitReached, "RECOVERY_ATTEMPT_LIMIT_REACHED"
            ]

    let rejectionCode value = Map.find value rejectionCodeMap

    let faultCode =
        function
        | FaultCode.StoreUnavailable -> "STORE_UNAVAILABLE"
        | FaultCode.StoreIntegrityError -> "STORE_INTEGRITY_ERROR"
        | FaultCode.SchemaMismatch -> "SCHEMA_MISMATCH"
        | FaultCode.RecoveryCapacityExceeded -> "RECOVERY_CAPACITY_EXCEEDED"
        | FaultCode.RecoveryIntegrityError -> "RECOVERY_INTEGRITY_ERROR"
        | FaultCode.CommitOutcomeUnknown -> "COMMIT_OUTCOME_UNKNOWN"
        | FaultCode.TechnicalMutationUnknown -> "TECHNICAL_MUTATION_UNKNOWN"

    let recoveryRejectionCode =
        function
        | RecoveryRejectionCode.InvalidRecoveryInput -> "INVALID_RECOVERY_INPUT"
        | RecoveryRejectionCode.PreparationNotFound -> "PREPARATION_NOT_FOUND"
        | RecoveryRejectionCode.RecoveryIdempotencyConflict -> "RECOVERY_IDEMPOTENCY_CONFLICT"
        | RecoveryRejectionCode.PreparationDismissed -> "PREPARATION_DISMISSED"
        | RecoveryRejectionCode.SubmissionAlreadyStarted -> "SUBMISSION_ALREADY_STARTED"
        | RecoveryRejectionCode.RecoveryActionUnavailable -> "RECOVERY_ACTION_UNAVAILABLE"
        | RecoveryRejectionCode.SourceDigestMismatch -> "SOURCE_DIGEST_MISMATCH"
        | RecoveryRejectionCode.InstallationMismatch -> "INSTALLATION_MISMATCH"
        | RecoveryRejectionCode.UnsupportedRecoveryArtifact -> "UNSUPPORTED_RECOVERY_ARTIFACT"
        | RecoveryRejectionCode.OperationRevoked -> "OPERATION_REVOKED"
        | RecoveryRejectionCode.AttemptLimitReached -> "ATTEMPT_LIMIT_REACHED"

    let preparationState =
        function
        | PreparationState.Unsubmitted -> "UNSUBMITTED"
        | PreparationState.SubmissionStarted -> "SUBMISSION_STARTED"
        | PreparationState.Dismissed -> "DISMISSED"
        | PreparationState.Revoked -> "REVOKED"

    let recoveryListView =
        function
        | RecoveryListView.Pending -> "PENDING"
        | RecoveryListView.Terminal -> "TERMINAL"

    let recoveryAuthority =
        function
        | RecoveryAuthority.PendingAuthority -> "PENDING"
        | RecoveryAuthority.AcceptedAuthority -> "ACCEPTED"
        | RecoveryAuthority.RevokedAuthority -> "REVOKED"

    let recoveryAction =
        function
        | RecoveryAction.Resolve -> "RESOLVE"
        | RecoveryAction.Dismiss -> "DISMISS"
        | RecoveryAction.Export -> "EXPORT"

    let webArtifactKind =
        function
        | RecoveryArtifactKind.Envelope -> "ENVELOPE"
        | RecoveryArtifactKind.UnboundCanonicalRecord -> "UNBOUND_CANONICAL_RECORD"

    let cliArtifactKind =
        function
        | RecoveryArtifactKind.Envelope -> "ENVELOPE"
        | RecoveryArtifactKind.UnboundCanonicalRecord -> "CANONICAL_RECORD"

    let actions =
        [
            RecommendedAction.CorrectInput
            RecommendedAction.ReadCurrent
            RecommendedAction.RetrySafe
            RecommendedAction.RecoverExact
            RecommendedAction.Reauthenticate
            RecommendedAction.StopAndInvestigate
            RecommendedAction.NoneRequired
        ]
        |> List.map action

    let rejectionCodes =
        [
            RejectionCode.InvalidInput
            RejectionCode.CaseNotFound
            RejectionCode.CaseAlreadyExists
            RejectionCode.VersionConflict
            RejectionCode.CaseClosed
            RejectionCode.AmendmentRequiresUndecided
            RejectionCode.DecisionRequired
            RejectionCode.PaymentAlreadyRecorded
            RejectionCode.PaymentNotRecorded
            RejectionCode.DecisionAlreadyPaid
            RejectionCode.AlreadyClosed
            RejectionCode.AlreadyOpened
            RejectionCode.ZeroDecisionCannotBePaid
            RejectionCode.IdempotencyConflict
            RejectionCode.OperationRevoked
            RejectionCode.RecoveryAttemptLimitReached
        ]
        |> List.map rejectionCode

    let faultCodes =
        [
            FaultCode.StoreUnavailable
            FaultCode.StoreIntegrityError
            FaultCode.SchemaMismatch
            FaultCode.RecoveryCapacityExceeded
            FaultCode.RecoveryIntegrityError
            FaultCode.CommitOutcomeUnknown
            FaultCode.TechnicalMutationUnknown
        ]
        |> List.map faultCode

    let recoveryRejectionCodes =
        [
            RecoveryRejectionCode.InvalidRecoveryInput
            RecoveryRejectionCode.PreparationNotFound
            RecoveryRejectionCode.RecoveryIdempotencyConflict
            RecoveryRejectionCode.PreparationDismissed
            RecoveryRejectionCode.SubmissionAlreadyStarted
            RecoveryRejectionCode.RecoveryActionUnavailable
            RecoveryRejectionCode.SourceDigestMismatch
            RecoveryRejectionCode.InstallationMismatch
            RecoveryRejectionCode.UnsupportedRecoveryArtifact
            RecoveryRejectionCode.OperationRevoked
            RecoveryRejectionCode.AttemptLimitReached
        ]
        |> List.map recoveryRejectionCode
