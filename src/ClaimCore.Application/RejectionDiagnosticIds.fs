namespace ClaimCore.Application

/// Explicit, stable identities. Tokens never derive from a union name or human explanation.
[<RequireQualifiedAccess>]
type RejectionDiagnosticId =
    | TextRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TextTooShort
    | TextTooLong
    | ControlCharacters
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate
    | DecimalFormat
    | CurrencyFormat
    | AmountNotRepresentable
    | EmptyOperationId
    | ExpectedVersionOutOfRange
    | StoredVersionOutOfRange
    | RevisionExhausted
    | CaseReferenceMismatch
    | StateTransitionMismatch
    | ExactInputsRequired
    | GroupedCorrectionRequired
    | CompleteDecisionRequired
    | PaymentClearRequired
    | PaymentAcknowledgementRequired
    | PaymentDecisionRequired
    | ReplacementFieldsRequired
    | RegistrationCannotBeCleared
    | CaseNotFound
    | CaseAlreadyExists
    | VersionConflict
    | CaseClosed
    | AlreadyClosed
    | AlreadyOpened
    | AmendmentRequiresUndecided
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | DecisionRequired
    | PaymentAlreadyRecorded
    | PaymentNotRecorded
    | DecisionAlreadyPaid
    | ZeroDecisionCannotBePaid
    | PageLimitOutOfRange
    | InvalidHistoryCursor
    | IdempotencyConflict
    | OperationRevoked
    | RecoveryAttemptLimitReached

/// Safe metadata only: these are constraint limits, not submitted data or arbitrary string values.
type DiagnosticParameterDefinition =
    {
        Name: string
        Minimum: int
        Maximum: int
    }

type RejectionDiagnosticDefinition =
    {
        Id: string
        Parameters: DiagnosticParameterDefinition list
    }

module RejectionDiagnosticIds =
    let private text =
        [
            RejectionDiagnosticId.TextRequired, "INPUT_TEXT_REQUIRED"
            RejectionDiagnosticId.MalformedUnicode, "INPUT_MALFORMED_UNICODE"
            RejectionDiagnosticId.SurroundingWhitespace, "INPUT_SURROUNDING_WHITESPACE"
            RejectionDiagnosticId.TextTooShort, "INPUT_TEXT_TOO_SHORT"
            RejectionDiagnosticId.TextTooLong, "INPUT_TEXT_TOO_LONG"
            RejectionDiagnosticId.ControlCharacters, "INPUT_CONTROL_CHARACTERS"
        ]

    let private scalar =
        [
            RejectionDiagnosticId.CalendarDateRequired, "INPUT_CALENDAR_DATE_REQUIRED"
            RejectionDiagnosticId.ChronologicalOrder, "INPUT_DATE_ORDER"
            RejectionDiagnosticId.FutureDate, "INPUT_FUTURE_DATE"
            RejectionDiagnosticId.DecimalFormat, "INPUT_DECIMAL_FORMAT"
            RejectionDiagnosticId.CurrencyFormat, "INPUT_CURRENCY_FORMAT"
            RejectionDiagnosticId.AmountNotRepresentable, "INPUT_AMOUNT_NOT_REPRESENTABLE"
        ]

    let private command =
        [
            RejectionDiagnosticId.EmptyOperationId, "COMMAND_OPERATION_ID_REQUIRED"
            RejectionDiagnosticId.ExpectedVersionOutOfRange, "COMMAND_EXPECTED_REVISION_RANGE"
            RejectionDiagnosticId.StoredVersionOutOfRange, "STATE_STORED_REVISION_RANGE"
            RejectionDiagnosticId.RevisionExhausted, "STATE_REVISION_EXHAUSTED"
            RejectionDiagnosticId.CaseReferenceMismatch, "STATE_CASE_REFERENCE_MISMATCH"
            RejectionDiagnosticId.StateTransitionMismatch, "STATE_TRANSITION_MISMATCH"
            RejectionDiagnosticId.ExactInputsRequired, "INPUT_EXACT_FIELDS_REQUIRED"
            RejectionDiagnosticId.GroupedCorrectionRequired, "COMMAND_GROUPED_CORRECTION_REQUIRED"
        ]

    let private correction =
        [
            RejectionDiagnosticId.CompleteDecisionRequired, "CORRECTION_COMPLETE_DECISION_REQUIRED"
            RejectionDiagnosticId.PaymentClearRequired, "CORRECTION_PAYMENT_CLEAR_REQUIRED"
            RejectionDiagnosticId.PaymentAcknowledgementRequired,
            "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"
            RejectionDiagnosticId.PaymentDecisionRequired, "CORRECTION_PAYMENT_DECISION_REQUIRED"
            RejectionDiagnosticId.ReplacementFieldsRequired,
            "CORRECTION_REPLACEMENT_FIELDS_REQUIRED"
            RejectionDiagnosticId.RegistrationCannotBeCleared,
            "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN"
        ]

    let private case =
        [
            RejectionDiagnosticId.CaseNotFound, "CASE_NOT_FOUND"
            RejectionDiagnosticId.CaseAlreadyExists, "CASE_ALREADY_EXISTS"
            RejectionDiagnosticId.VersionConflict, "CASE_REVISION_CONFLICT"
            RejectionDiagnosticId.CaseClosed, "CASE_CLOSED"
            RejectionDiagnosticId.AlreadyClosed, "CASE_ALREADY_CLOSED"
            RejectionDiagnosticId.AlreadyOpened, "CASE_ALREADY_OPENED"
        ]

    let private progress =
        [
            RejectionDiagnosticId.AmendmentRequiresUndecided, "CASE_AMENDMENT_REQUIRES_UNDECIDED"
            RejectionDiagnosticId.CorrectionNoChanges, "CORRECTION_NO_CHANGES"
            RejectionDiagnosticId.CorrectionRequiresExistingValue,
            "CORRECTION_EXISTING_VALUE_REQUIRED"
            RejectionDiagnosticId.DecisionRequired, "CASE_DECISION_REQUIRED"
            RejectionDiagnosticId.PaymentAlreadyRecorded, "CASE_PAYMENT_ALREADY_RECORDED"
            RejectionDiagnosticId.PaymentNotRecorded, "CASE_PAYMENT_NOT_RECORDED"
            RejectionDiagnosticId.DecisionAlreadyPaid, "CASE_DECISION_ALREADY_PAID"
            RejectionDiagnosticId.ZeroDecisionCannotBePaid, "CASE_ZERO_DECISION_CANNOT_BE_PAID"
        ]

    let private operation =
        [
            RejectionDiagnosticId.PageLimitOutOfRange, "QUERY_PAGE_LIMIT_RANGE"
            RejectionDiagnosticId.InvalidHistoryCursor, "QUERY_HISTORY_CURSOR_INVALID"
            RejectionDiagnosticId.IdempotencyConflict, "OPERATION_CONTENT_CONFLICT"
            RejectionDiagnosticId.OperationRevoked, "OPERATION_REVOKED"
            RejectionDiagnosticId.RecoveryAttemptLimitReached, "OPERATION_RECOVERY_ATTEMPT_LIMIT"
        ]

    let all = text @ scalar @ command @ correction @ case @ progress @ operation

    let token identifier =
        all |> List.find (fst >> (=) identifier) |> snd

    let private parameter name minimum =
        {
            Name = name
            Minimum = minimum
            Maximum = System.Int32.MaxValue
        }

    let parameters =
        function
        | RejectionDiagnosticId.TextTooShort -> [ parameter "minimumCharacters" 0 ]
        | RejectionDiagnosticId.TextTooLong -> [ parameter "maximumCharacters" 0 ]
        | RejectionDiagnosticId.DecimalFormat ->
            [ parameter "maximumIntegerDigits" 1; parameter "maximumFractionalDigits" 0 ]
        | RejectionDiagnosticId.PageLimitOutOfRange -> [ parameter "maximumPageSize" 1 ]
        | _ -> []

    let definitions =
        all
        |> List.map (fun (identifier, value) ->
            {
                Id = value
                Parameters = parameters identifier
            })
