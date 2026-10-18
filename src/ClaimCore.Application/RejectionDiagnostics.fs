namespace ClaimCore.Application

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type DiagnosticParameters =
    | None
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
        | TextViolation.TooLong maximum ->
            create
                RejectionDiagnosticId.TextTooLong
                (DiagnosticParameters.MaximumCharacters maximum)
        | TextViolation.ControlCharacters -> simple RejectionDiagnosticId.ControlCharacters
        | TextViolation.FormattingCharacter -> simple RejectionDiagnosticId.FormattingCharacter
        | TextViolation.ReferenceFormat -> simple RejectionDiagnosticId.ReferenceFormat
        | TextViolation.CountryFormat -> simple RejectionDiagnosticId.CountryFormat

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

    let private command =
        function
        | ActViolation.EmptyOperationId -> simple RejectionDiagnosticId.EmptyOperationId
        | ActViolation.ExpectedRevisionOutOfRange ->
            simple RejectionDiagnosticId.ExpectedRevisionOutOfRange

    let private correction =
        function
        | CorrectionViolation.PaymentClearRequired ->
            simple RejectionDiagnosticId.PaymentClearRequired
        | CorrectionViolation.PaymentAcknowledgementRequired ->
            simple RejectionDiagnosticId.PaymentAcknowledgementRequired

    let private input =
        function
        | InputViolation.Text value -> text value
        | InputViolation.Date value -> date value
        | InputViolation.Amount value -> amount value
        | InputViolation.Act value -> command value
        | InputViolation.Correction value -> correction value

    let private progress =
        function
        | CaseRefusal.AmendmentRequiresUndecided ->
            simple RejectionDiagnosticId.AmendmentRequiresUndecided
        | CaseRefusal.CorrectionNoChanges -> simple RejectionDiagnosticId.CorrectionNoChanges
        | CaseRefusal.CorrectionRequiresExistingValue ->
            simple RejectionDiagnosticId.CorrectionRequiresExistingValue
        | CaseRefusal.DecisionRequired -> simple RejectionDiagnosticId.DecisionRequired
        | CaseRefusal.PaymentAlreadyRecorded -> simple RejectionDiagnosticId.PaymentAlreadyRecorded
        | CaseRefusal.PaymentNotRecorded -> simple RejectionDiagnosticId.PaymentNotRecorded
        | CaseRefusal.DecisionAlreadyPaid -> simple RejectionDiagnosticId.DecisionAlreadyPaid
        | CaseRefusal.ZeroDecisionCannotBePaid ->
            simple RejectionDiagnosticId.ZeroDecisionCannotBePaid
        | _ -> invalidArg "error" "Expected a progress refusal."

    let private lifecycle =
        function
        | CaseRefusal.CaseVoided -> simple RejectionDiagnosticId.CaseVoided
        | CaseRefusal.CaseErased -> simple RejectionDiagnosticId.CaseErased
        | CaseRefusal.AlreadyVoided -> simple RejectionDiagnosticId.AlreadyVoided
        | CaseRefusal.NotVoided -> simple RejectionDiagnosticId.NotVoided
        | CaseRefusal.RevisionExhausted -> simple RejectionDiagnosticId.RevisionExhausted
        | value -> progress value

    let private domain =
        function
        | CaseRefusal.InvalidInput(_, value) -> input value
        | CaseRefusal.NotFound -> simple RejectionDiagnosticId.CaseNotFound
        | CaseRefusal.AlreadyExists -> simple RejectionDiagnosticId.CaseAlreadyExists
        | CaseRefusal.RevisionConflict _ -> simple RejectionDiagnosticId.RevisionConflict
        | CaseRefusal.ClosedCase -> simple RejectionDiagnosticId.CaseClosed
        | CaseRefusal.AlreadyClosed -> simple RejectionDiagnosticId.AlreadyClosed
        | CaseRefusal.AlreadyOpened -> simple RejectionDiagnosticId.AlreadyOpened
        | value -> lifecycle value

    let describe =
        function
        | Rejection.Case value -> domain value
        | Rejection.PageLimitOutOfRange maximum ->
            create
                RejectionDiagnosticId.PageLimitOutOfRange
                (DiagnosticParameters.MaximumPageSize maximum)
        | reason ->
            Rejections.simplePolicies
            |> List.find (fun (value, _) -> value = reason)
            |> fun (_, identifier) -> simple identifier

    let identifier (diagnostic: RejectionDiagnostic) = diagnostic.Identifier
    let parameters (diagnostic: RejectionDiagnostic) = diagnostic.Parameters

    /// Pure read projection only, not a constructor or input boundary.
    let values (diagnostic: RejectionDiagnostic) =
        match diagnostic.Parameters with
        | DiagnosticParameters.None -> []
        | DiagnosticParameters.MaximumCharacters value -> [ "maximumCharacters", value ]
        | DiagnosticParameters.DecimalDigits(integer, fraction) ->
            [ "maximumIntegerDigits", integer; "maximumFractionalDigits", fraction ]
        | DiagnosticParameters.MaximumPageSize value -> [ "maximumPageSize", value ]
