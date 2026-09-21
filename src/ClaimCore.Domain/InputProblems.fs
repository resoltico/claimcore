namespace ClaimCore.Domain

/// Closed diagnostic locations. No submitted field name, path, or value can be echoed as a target.
[<RequireQualifiedAccess>]
type InputTarget =
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | Status
    | OperationId
    | ExpectedVersion
    | Version
    | Command
    | Fields
    | Registration
    | Decision
    | Payment

module InputTargets =
    let all =
        [
            InputTarget.IncidentDate, "incidentDate"
            InputTarget.IncidentNotificationDate, "incidentNotificationDate"
            InputTarget.IncidentCountry, "incidentCountry"
            InputTarget.ClaimantName, "claimantName"
            InputTarget.InsurerName, "insurerName"
            InputTarget.ClaimedAmount, "claimedAmount"
            InputTarget.ClaimedCurrency, "claimedCurrency"
            InputTarget.CaseReference, "caseReference"
            InputTarget.PaymentDecisionDate, "paymentDecisionDate"
            InputTarget.PayableAmount, "payableAmount"
            InputTarget.PayableCurrency, "payableCurrency"
            InputTarget.PaymentDate, "paymentDate"
            InputTarget.Status, "status"
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedVersion, "expectedRevision"
            InputTarget.Version, "revision"
            InputTarget.Command, "command"
            InputTarget.Fields, "fields"
            InputTarget.Registration, "registration"
            InputTarget.Decision, "decision"
            InputTarget.Payment, "payment"
        ]

    let token target =
        all |> List.find (fst >> (=) target) |> snd

[<RequireQualifiedAccess>]
type TextViolation =
    | NonBlankRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TooShort of minimumCharacters: int
    | TooLong of maximumCharacters: int
    | ControlCharacters

[<RequireQualifiedAccess>]
type DateViolation =
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate

[<RequireQualifiedAccess>]
type AmountViolation =
    | DecimalFormat of maximumIntegerDigits: int * maximumFractionalDigits: int
    | CurrencyFormat
    | NotRepresentable

[<RequireQualifiedAccess>]
type CommandViolation =
    | EmptyOperationId
    | ExpectedVersionOutOfRange
    | StoredVersionOutOfRange
    | RevisionExhausted
    | CaseReferenceMismatch
    | StateTransitionMismatch
    | ExactInputsRequired
    | GroupedCorrectionRequired

[<RequireQualifiedAccess>]
type CorrectionViolation =
    | CompleteDecisionRequired
    | PaymentClearRequired
    | PaymentAcknowledgementRequired
    | PaymentDecisionRequired
    | ReplacementFieldsRequired
    | RegistrationCannotBeCleared

/// Closed reason with only typed constraint limits. Authored content is never a diagnostic argument.
[<RequireQualifiedAccess>]
type InputViolation =
    | Text of TextViolation
    | Date of DateViolation
    | Amount of AmountViolation
    | Command of CommandViolation
    | Correction of CorrectionViolation
