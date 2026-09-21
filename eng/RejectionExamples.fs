module internal ClaimCore.Qualification.RejectionExamples

open ClaimCore.Application
open ClaimCore.Domain

// Reviewed synthetic representatives, linked only into tooling and tests, never the product.
let private invalid target violation =
    Rejection.Domain(DomainError.InvalidInput(target, violation))

let private text =
    [
        "INPUT_TEXT_REQUIRED",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.NonBlankRequired))
        "INPUT_MALFORMED_UNICODE",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.MalformedUnicode))
        "INPUT_SURROUNDING_WHITESPACE",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.SurroundingWhitespace))
        "INPUT_TEXT_TOO_SHORT",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.TooShort 1))
        "INPUT_TEXT_TOO_LONG",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.TooLong 200))
        "INPUT_CONTROL_CHARACTERS",
        invalid InputTarget.ClaimantName (InputViolation.Text(TextViolation.ControlCharacters))
    ]

let private date =
    [
        "INPUT_CALENDAR_DATE_REQUIRED",
        invalid InputTarget.IncidentDate (InputViolation.Date(DateViolation.CalendarDateRequired))
        "INPUT_DATE_ORDER",
        invalid InputTarget.IncidentDate (InputViolation.Date(DateViolation.ChronologicalOrder))
        "INPUT_FUTURE_DATE",
        invalid InputTarget.IncidentDate (InputViolation.Date(DateViolation.FutureDate))
    ]

let private amount =
    [
        "INPUT_DECIMAL_FORMAT",
        invalid
            InputTarget.ClaimedAmount
            (InputViolation.Amount(AmountViolation.DecimalFormat(18, 4)))
        "INPUT_CURRENCY_FORMAT",
        invalid InputTarget.ClaimedAmount (InputViolation.Amount(AmountViolation.CurrencyFormat))
        "INPUT_AMOUNT_NOT_REPRESENTABLE",
        invalid InputTarget.ClaimedAmount (InputViolation.Amount(AmountViolation.NotRepresentable))
    ]

let private command =
    [
        "COMMAND_OPERATION_ID_REQUIRED",
        invalid InputTarget.Command (InputViolation.Command(CommandViolation.EmptyOperationId))
        "COMMAND_EXPECTED_REVISION_RANGE",
        invalid
            InputTarget.Command
            (InputViolation.Command(CommandViolation.ExpectedVersionOutOfRange))
        "STATE_STORED_REVISION_RANGE",
        invalid
            InputTarget.Command
            (InputViolation.Command(CommandViolation.StoredVersionOutOfRange))
        "STATE_REVISION_EXHAUSTED",
        invalid InputTarget.Command (InputViolation.Command(CommandViolation.RevisionExhausted))
        "STATE_CASE_REFERENCE_MISMATCH",
        invalid InputTarget.Command (InputViolation.Command(CommandViolation.CaseReferenceMismatch))
        "STATE_TRANSITION_MISMATCH",
        invalid
            InputTarget.Command
            (InputViolation.Command(CommandViolation.StateTransitionMismatch))
        "INPUT_EXACT_FIELDS_REQUIRED",
        invalid InputTarget.Command (InputViolation.Command(CommandViolation.ExactInputsRequired))
        "COMMAND_GROUPED_CORRECTION_REQUIRED",
        invalid
            InputTarget.Command
            (InputViolation.Command(CommandViolation.GroupedCorrectionRequired))
    ]

let private correction =
    [
        "CORRECTION_COMPLETE_DECISION_REQUIRED",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.CompleteDecisionRequired))
        "CORRECTION_PAYMENT_CLEAR_REQUIRED",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.PaymentClearRequired))
        "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.PaymentAcknowledgementRequired))
        "CORRECTION_PAYMENT_DECISION_REQUIRED",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.PaymentDecisionRequired))
        "CORRECTION_REPLACEMENT_FIELDS_REQUIRED",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.ReplacementFieldsRequired))
        "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN",
        invalid
            InputTarget.Decision
            (InputViolation.Correction(CorrectionViolation.RegistrationCannotBeCleared))
    ]

let private state =
    [
        "CASE_NOT_FOUND", Rejection.Domain(DomainError.NotFound)
        "CASE_ALREADY_EXISTS", Rejection.Domain(DomainError.AlreadyExists)
        "CASE_REVISION_CONFLICT", Rejection.Domain(DomainError.VersionConflict 2L)
        "CASE_CLOSED", Rejection.Domain(DomainError.ClosedCase)
        "CASE_ALREADY_CLOSED", Rejection.Domain(DomainError.AlreadyClosed)
        "CASE_ALREADY_OPENED", Rejection.Domain(DomainError.AlreadyOpened)
        "CASE_AMENDMENT_REQUIRES_UNDECIDED",
        Rejection.Domain(DomainError.AmendmentRequiresUndecided)
        "CORRECTION_NO_CHANGES", Rejection.Domain(DomainError.CorrectionNoChanges)
        "CORRECTION_EXISTING_VALUE_REQUIRED",
        Rejection.Domain(DomainError.CorrectionRequiresExistingValue)
        "CASE_DECISION_REQUIRED", Rejection.Domain(DomainError.DecisionRequired)
        "CASE_PAYMENT_ALREADY_RECORDED", Rejection.Domain(DomainError.PaymentAlreadyRecorded)
        "CASE_PAYMENT_NOT_RECORDED", Rejection.Domain(DomainError.PaymentNotRecorded)
        "CASE_DECISION_ALREADY_PAID", Rejection.Domain(DomainError.DecisionAlreadyPaid)
        "CASE_ZERO_DECISION_CANNOT_BE_PAID", Rejection.Domain(DomainError.ZeroDecisionCannotBePaid)
    ]

let private application =
    [
        "QUERY_PAGE_LIMIT_RANGE", Rejection.PageLimitOutOfRange 50
        "QUERY_HISTORY_CURSOR_INVALID", Rejection.InvalidHistoryCursor
        "OPERATION_CONTENT_CONFLICT", Rejection.IdempotencyConflict
        "OPERATION_REVOKED", Rejection.OperationRevoked
        "OPERATION_RECOVERY_ATTEMPT_LIMIT", Rejection.RecoveryAttemptLimitReached
    ]

let all = text @ date @ amount @ command @ correction @ state @ application
