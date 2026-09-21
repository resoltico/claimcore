namespace ClaimCore.Application

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type DiagnosticParameters =
    | None
    | MinimumCharacters of int
    | MaximumCharacters of int
    | DecimalDigits of maximumIntegerDigits: int * maximumFractionalDigits: int
    | MaximumPageSize of int

type RejectionDiagnostic =
    private
        {
            Identifier: RejectionDiagnosticId
            Parameters: DiagnosticParameters
        }

module RejectionDiagnostics =
    let private create identifier parameters =
        {
            Identifier = identifier
            Parameters = parameters
        }

    let private simple identifier =
        create identifier DiagnosticParameters.None

    let private text =
        function
        | TextViolation.NonBlankRequired -> simple RejectionDiagnosticId.TextRequired
        | TextViolation.MalformedUnicode -> simple RejectionDiagnosticId.MalformedUnicode
        | TextViolation.SurroundingWhitespace -> simple RejectionDiagnosticId.SurroundingWhitespace
        | TextViolation.TooShort minimum ->
            create
                RejectionDiagnosticId.TextTooShort
                (DiagnosticParameters.MinimumCharacters minimum)
        | TextViolation.TooLong maximum ->
            create
                RejectionDiagnosticId.TextTooLong
                (DiagnosticParameters.MaximumCharacters maximum)
        | TextViolation.ControlCharacters -> simple RejectionDiagnosticId.ControlCharacters

    let private date =
        function
        | DateViolation.CalendarDateRequired -> simple RejectionDiagnosticId.CalendarDateRequired
        | DateViolation.ChronologicalOrder -> simple RejectionDiagnosticId.ChronologicalOrder
        | DateViolation.FutureDate -> simple RejectionDiagnosticId.FutureDate

    let private amount =
        function
        | AmountViolation.DecimalFormat(integer, fraction) ->
            create
                RejectionDiagnosticId.DecimalFormat
                (DiagnosticParameters.DecimalDigits(integer, fraction))
        | AmountViolation.CurrencyFormat -> simple RejectionDiagnosticId.CurrencyFormat
        | AmountViolation.NotRepresentable -> simple RejectionDiagnosticId.AmountNotRepresentable

    let private command =
        function
        | CommandViolation.EmptyOperationId -> simple RejectionDiagnosticId.EmptyOperationId
        | CommandViolation.ExpectedVersionOutOfRange ->
            simple RejectionDiagnosticId.ExpectedVersionOutOfRange
        | CommandViolation.StoredVersionOutOfRange ->
            simple RejectionDiagnosticId.StoredVersionOutOfRange
        | CommandViolation.RevisionExhausted -> simple RejectionDiagnosticId.RevisionExhausted
        | CommandViolation.CaseReferenceMismatch ->
            simple RejectionDiagnosticId.CaseReferenceMismatch
        | CommandViolation.StateTransitionMismatch ->
            simple RejectionDiagnosticId.StateTransitionMismatch
        | CommandViolation.ExactInputsRequired -> simple RejectionDiagnosticId.ExactInputsRequired
        | CommandViolation.GroupedCorrectionRequired ->
            simple RejectionDiagnosticId.GroupedCorrectionRequired

    let private correction =
        function
        | CorrectionViolation.CompleteDecisionRequired ->
            simple RejectionDiagnosticId.CompleteDecisionRequired
        | CorrectionViolation.PaymentClearRequired ->
            simple RejectionDiagnosticId.PaymentClearRequired
        | CorrectionViolation.PaymentAcknowledgementRequired ->
            simple RejectionDiagnosticId.PaymentAcknowledgementRequired
        | CorrectionViolation.PaymentDecisionRequired ->
            simple RejectionDiagnosticId.PaymentDecisionRequired
        | CorrectionViolation.ReplacementFieldsRequired ->
            simple RejectionDiagnosticId.ReplacementFieldsRequired
        | CorrectionViolation.RegistrationCannotBeCleared ->
            simple RejectionDiagnosticId.RegistrationCannotBeCleared

    let private input =
        function
        | InputViolation.Text value -> text value
        | InputViolation.Date value -> date value
        | InputViolation.Amount value -> amount value
        | InputViolation.Command value -> command value
        | InputViolation.Correction value -> correction value

    let private progress =
        function
        | DomainError.AmendmentRequiresUndecided ->
            simple RejectionDiagnosticId.AmendmentRequiresUndecided
        | DomainError.CorrectionNoChanges -> simple RejectionDiagnosticId.CorrectionNoChanges
        | DomainError.CorrectionRequiresExistingValue ->
            simple RejectionDiagnosticId.CorrectionRequiresExistingValue
        | DomainError.DecisionRequired -> simple RejectionDiagnosticId.DecisionRequired
        | DomainError.PaymentAlreadyRecorded -> simple RejectionDiagnosticId.PaymentAlreadyRecorded
        | DomainError.PaymentNotRecorded -> simple RejectionDiagnosticId.PaymentNotRecorded
        | DomainError.DecisionAlreadyPaid -> simple RejectionDiagnosticId.DecisionAlreadyPaid
        | DomainError.ZeroDecisionCannotBePaid ->
            simple RejectionDiagnosticId.ZeroDecisionCannotBePaid
        | _ -> invalidArg "error" "Expected a progress refusal."

    let private domain =
        function
        | DomainError.InvalidInput(_, value) -> input value
        | DomainError.NotFound -> simple RejectionDiagnosticId.CaseNotFound
        | DomainError.AlreadyExists -> simple RejectionDiagnosticId.CaseAlreadyExists
        | DomainError.VersionConflict _ -> simple RejectionDiagnosticId.VersionConflict
        | DomainError.ClosedCase -> simple RejectionDiagnosticId.CaseClosed
        | DomainError.AlreadyClosed -> simple RejectionDiagnosticId.AlreadyClosed
        | DomainError.AlreadyOpened -> simple RejectionDiagnosticId.AlreadyOpened
        | value -> progress value

    let describe =
        function
        | Rejection.Domain value -> domain value
        | Rejection.PageLimitOutOfRange maximum ->
            create
                RejectionDiagnosticId.PageLimitOutOfRange
                (DiagnosticParameters.MaximumPageSize maximum)
        | Rejection.InvalidHistoryCursor -> simple RejectionDiagnosticId.InvalidHistoryCursor
        | Rejection.IdempotencyConflict -> simple RejectionDiagnosticId.IdempotencyConflict
        | Rejection.OperationRevoked -> simple RejectionDiagnosticId.OperationRevoked
        | Rejection.RecoveryAttemptLimitReached ->
            simple RejectionDiagnosticId.RecoveryAttemptLimitReached

    let identifier (diagnostic: RejectionDiagnostic) = diagnostic.Identifier
    let parameters (diagnostic: RejectionDiagnostic) = diagnostic.Parameters

    /// Pure read projection only, not a constructor or input boundary.
    let values (diagnostic: RejectionDiagnostic) =
        match diagnostic.Parameters with
        | DiagnosticParameters.None -> []
        | DiagnosticParameters.MinimumCharacters value -> [ "minimumCharacters", value ]
        | DiagnosticParameters.MaximumCharacters value -> [ "maximumCharacters", value ]
        | DiagnosticParameters.DecimalDigits(integer, fraction) ->
            [ "maximumIntegerDigits", integer; "maximumFractionalDigits", fraction ]
        | DiagnosticParameters.MaximumPageSize value -> [ "maximumPageSize", value ]
