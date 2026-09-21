namespace ClaimCore.Contracts

open ClaimCore.Application

/// Default presentation only; wording is excluded from diagnostic identity.
module RecoveryRejectionPresentation =
    let private group0 =
        [
            RecoveryRejection.OperationIdRequired, "A non-empty recovery operation ID is required."
            RecoveryRejection.RequestDigestInvalid,
            "The recovery request digest must be a lowercase SHA-256 digest."
            RecoveryRejection.PageLimitOutOfRange,
            "The recovery page limit is outside the supported range."
            RecoveryRejection.ListCursorInvalid, "The recovery list cursor is invalid."
            RecoveryRejection.ListCursorViewMismatch,
            "The recovery list cursor belongs to a different view."
            RecoveryRejection.AttemptCursorInvalid, "The recovery attempt cursor is invalid."
        ]

    let private group1 =
        [
            RecoveryRejection.AttemptCursorOperationMismatch,
            "The recovery attempt cursor belongs to a different operation."
            RecoveryRejection.DismissalConfirmationRequired,
            "Explicit confirmation is required to dismiss a recovery preparation."
            RecoveryRejection.PreparationNotFound, "The recovery preparation was not found."
            RecoveryRejection.ContentConflict,
            "The supplied recovery identity conflicts with an existing operation."
            RecoveryRejection.PreparationDismissed, "A dismissed preparation cannot be submitted."
            RecoveryRejection.SubmissionAlreadyStarted,
            "The preparation has already started submission and must be resolved exactly."
        ]

    let private group2 =
        [
            RecoveryRejection.AcceptedOperationCannotBeDismissed,
            "An accepted operation cannot be dismissed. Inspect its retained receipt."
            RecoveryRejection.SourceDigestMismatch,
            "The supplied source digest does not match the exact import bytes."
            RecoveryRejection.RequestDigestMismatch,
            "The supplied digest does not match the exact retained request bytes."
            RecoveryRejection.InstallationMismatch,
            "The recovery envelope belongs to a different ClaimCore installation."
            RecoveryRejection.EnvelopeInvalidOrUnsupported,
            "The recovery envelope is unsupported or failed canonical decoding."
            RecoveryRejection.CanonicalRecordInvalidOrUnsupported,
            "The canonical recovery record is unsupported or failed canonical decoding."
        ]

    let private group3 =
        [
            RecoveryRejection.OperationRevoked,
            "This exact operation was durably revoked before execution."
            RecoveryRejection.AttemptLimitReached,
            "This operation has reached its recovery attempt limit. Read the current case before authoring new work."
        ]

    let private explanations = group0 @ group1 @ group2 @ group3

    let render reason =
        explanations |> List.find (fst >> (=) reason) |> snd
