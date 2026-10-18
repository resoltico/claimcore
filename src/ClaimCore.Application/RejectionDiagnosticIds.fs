namespace ClaimCore.Application

/// Explicit, stable identities. Tokens never derive from a union name or human explanation.
[<RequireQualifiedAccess>]
type RejectionDiagnosticId =
    | TextRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TextTooLong
    | ControlCharacters
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate
    | DecimalFormat
    | CurrencyFormat
    | EmptyOperationId
    | ExpectedRevisionOutOfRange
    | RevisionExhausted
    | PaymentClearRequired
    | PaymentAcknowledgementRequired
    | CaseNotFound
    | CaseAlreadyExists
    | RevisionConflict
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
    | InvalidCursor
    | IdempotencyConflict
    | ResourceUnavailable
    | CaseVoided
    | CaseErased
    | AlreadyVoided
    | NotVoided
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired
    | FormattingCharacter
    | ReferenceFormat
    | CountryFormat
    | ActNotPermitted
    | LedgerExhausted

/// Safe metadata only: these are constraint limits, not submitted data or arbitrary string values.
type DiagnosticParameterDefinition =
    {
        Name: string
        Minimum: int
        Maximum: int
    }

type DiagnosticDefinition =
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
            RejectionDiagnosticId.TextTooLong, "INPUT_TEXT_TOO_LONG"
            RejectionDiagnosticId.ControlCharacters, "INPUT_CONTROL_CHARACTERS"
            RejectionDiagnosticId.FormattingCharacter, "TEXT_FORMATTING_CHARACTER"
            RejectionDiagnosticId.ReferenceFormat, "INPUT_CASE_REFERENCE_FORMAT"
            RejectionDiagnosticId.CountryFormat, "INPUT_COUNTRY_FORMAT"
        ]

    let private scalar =
        [
            RejectionDiagnosticId.CalendarDateRequired, "INPUT_CALENDAR_DATE_REQUIRED"
            RejectionDiagnosticId.ChronologicalOrder, "INPUT_DATE_ORDER"
            RejectionDiagnosticId.FutureDate, "INPUT_FUTURE_DATE"
            RejectionDiagnosticId.DecimalFormat, "INPUT_DECIMAL_FORMAT"
            RejectionDiagnosticId.CurrencyFormat, "INPUT_CURRENCY_FORMAT"
        ]

    let private command =
        [
            RejectionDiagnosticId.EmptyOperationId, "COMMAND_OPERATION_ID_REQUIRED"
            RejectionDiagnosticId.ExpectedRevisionOutOfRange, "COMMAND_EXPECTED_REVISION_RANGE"
            RejectionDiagnosticId.RevisionExhausted, "REVISION_EXHAUSTED"
        ]

    let private correction =
        [
            RejectionDiagnosticId.PaymentClearRequired, "CORRECTION_PAYMENT_CLEAR_REQUIRED"
            RejectionDiagnosticId.PaymentAcknowledgementRequired,
            "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"
        ]

    let private case =
        [
            RejectionDiagnosticId.CaseNotFound, "CASE_NOT_FOUND"
            RejectionDiagnosticId.CaseAlreadyExists, "CASE_ALREADY_EXISTS"
            RejectionDiagnosticId.RevisionConflict, "CASE_REVISION_CONFLICT"
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
            RejectionDiagnosticId.InvalidCursor, "QUERY_CURSOR_INVALID"
            RejectionDiagnosticId.ActNotPermitted, "ACT_NOT_PERMITTED"
            RejectionDiagnosticId.LedgerExhausted, "LEDGER_SEQUENCE_EXHAUSTED"
            RejectionDiagnosticId.IdempotencyConflict, "OPERATION_CONTENT_CONFLICT"
            RejectionDiagnosticId.ResourceUnavailable, "ACCESS_RESOURCE_UNAVAILABLE"
            RejectionDiagnosticId.CaseVoided, "CASE_VOIDED"
            RejectionDiagnosticId.CaseErased, "CASE_ERASED"
            RejectionDiagnosticId.AlreadyVoided, "ALREADY_VOIDED"
            RejectionDiagnosticId.NotVoided, "NOT_VOIDED"
            RejectionDiagnosticId.LastOwner, "LAST_OWNER"
            RejectionDiagnosticId.ActorAlreadyRegistered, "ACTOR_ALREADY_REGISTERED"
            RejectionDiagnosticId.ActorUnknown, "ACTOR_UNKNOWN"
            RejectionDiagnosticId.HumanPrincipalRequired, "HUMAN_PRINCIPAL_REQUIRED"
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
