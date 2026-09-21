namespace ClaimCore.Application

type RecoveryRejectionCode =
    | InvalidRecoveryInput
    | PreparationNotFound
    | RecoveryIdempotencyConflict
    | PreparationDismissed
    | SubmissionAlreadyStarted
    | RecoveryActionUnavailable
    | SourceDigestMismatch
    | InstallationMismatch
    | UnsupportedRecoveryArtifact
    | OperationRevoked
    | AttemptLimitReached

/// Closed, language-independent reason. Display text belongs to Contracts.
[<RequireQualifiedAccess>]
type RecoveryRejection =
    | OperationIdRequired
    | RequestDigestInvalid
    | PageLimitOutOfRange
    | ListCursorInvalid
    | ListCursorViewMismatch
    | AttemptCursorInvalid
    | AttemptCursorOperationMismatch
    | DismissalConfirmationRequired
    | PreparationNotFound
    | ContentConflict
    | PreparationDismissed
    | SubmissionAlreadyStarted
    | AcceptedOperationCannotBeDismissed
    | SourceDigestMismatch
    | RequestDigestMismatch
    | InstallationMismatch
    | EnvelopeInvalidOrUnsupported
    | CanonicalRecordInvalidOrUnsupported
    | OperationRevoked
    | AttemptLimitReached

/// One reviewed policy table owns identity and guidance together; presentation is external.
module RecoveryRejections =
    let private policy0 =
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

    let private policy1 =
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

    let private policy2 =
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

    let private policy3 =
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

type RecoveryRejection with
    member this.Code = RecoveryRejections.code this
    member this.Action = RecoveryRejections.action this
