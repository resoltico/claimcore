module ClaimCore.Tests.RecoveryDiagnosticExamples

open ClaimCore.Application

// Explicit reviewed vocabulary and guidance: no reflection-derived identifiers.
let private group0 =
    [
        RecoveryRejection.OperationIdRequired,
        "RECOVERY_OPERATION_ID_REQUIRED",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.RequestDigestInvalid,
        "RECOVERY_REQUEST_DIGEST_INVALID",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.PageLimitOutOfRange,
        "RECOVERY_PAGE_LIMIT_RANGE",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.ListCursorInvalid,
        "RECOVERY_LIST_CURSOR_INVALID",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.ListCursorViewMismatch,
        "RECOVERY_LIST_CURSOR_VIEW_MISMATCH",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.AttemptCursorInvalid,
        "RECOVERY_ATTEMPT_CURSOR_INVALID",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
    ]

let private group1 =
    [
        RecoveryRejection.AttemptCursorOperationMismatch,
        "RECOVERY_ATTEMPT_CURSOR_OPERATION_MISMATCH",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.DismissalConfirmationRequired,
        "RECOVERY_DISMISSAL_CONFIRMATION_REQUIRED",
        RecoveryRejectionCode.InvalidRecoveryInput,
        RecommendedAction.CorrectInput
        RecoveryRejection.PreparationNotFound,
        "RECOVERY_PREPARATION_NOT_FOUND",
        RecoveryRejectionCode.PreparationNotFound,
        RecommendedAction.ReadCurrent
        RecoveryRejection.ContentConflict,
        "RECOVERY_OPERATION_CONTENT_CONFLICT",
        RecoveryRejectionCode.RecoveryIdempotencyConflict,
        RecommendedAction.StopAndInvestigate
        RecoveryRejection.PreparationDismissed,
        "RECOVERY_PREPARATION_DISMISSED",
        RecoveryRejectionCode.PreparationDismissed,
        RecommendedAction.ReadCurrent
        RecoveryRejection.SubmissionAlreadyStarted,
        "RECOVERY_SUBMISSION_ALREADY_STARTED",
        RecoveryRejectionCode.SubmissionAlreadyStarted,
        RecommendedAction.RecoverExact
    ]

let private group2 =
    [
        RecoveryRejection.AcceptedOperationCannotBeDismissed,
        "RECOVERY_ACCEPTED_OPERATION_DISMISSAL_FORBIDDEN",
        RecoveryRejectionCode.RecoveryActionUnavailable,
        RecommendedAction.ReadCurrent
        RecoveryRejection.SourceDigestMismatch,
        "RECOVERY_SOURCE_DIGEST_MISMATCH",
        RecoveryRejectionCode.SourceDigestMismatch,
        RecommendedAction.StopAndInvestigate
        RecoveryRejection.RequestDigestMismatch,
        "RECOVERY_REQUEST_DIGEST_MISMATCH",
        RecoveryRejectionCode.SourceDigestMismatch,
        RecommendedAction.StopAndInvestigate
        RecoveryRejection.InstallationMismatch,
        "RECOVERY_INSTALLATION_MISMATCH",
        RecoveryRejectionCode.InstallationMismatch,
        RecommendedAction.StopAndInvestigate
        RecoveryRejection.EnvelopeInvalidOrUnsupported,
        "RECOVERY_ENVELOPE_INVALID_OR_UNSUPPORTED",
        RecoveryRejectionCode.UnsupportedRecoveryArtifact,
        RecommendedAction.CorrectInput
        RecoveryRejection.CanonicalRecordInvalidOrUnsupported,
        "RECOVERY_CANONICAL_RECORD_INVALID_OR_UNSUPPORTED",
        RecoveryRejectionCode.UnsupportedRecoveryArtifact,
        RecommendedAction.CorrectInput
    ]

let private group3 =
    [
        RecoveryRejection.OperationRevoked,
        "RECOVERY_OPERATION_REVOKED",
        RecoveryRejectionCode.OperationRevoked,
        RecommendedAction.ReadCurrent
        RecoveryRejection.AttemptLimitReached,
        "RECOVERY_ATTEMPT_LIMIT_REACHED",
        RecoveryRejectionCode.AttemptLimitReached,
        RecommendedAction.ReadCurrent
    ]

let all = group0 @ group1 @ group2 @ group3
