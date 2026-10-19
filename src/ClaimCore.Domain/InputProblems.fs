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
    | OperationId
    | ExpectedRevision
    | Payment
    | Reason

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
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedRevision, "expectedRevision"
            InputTarget.Payment, "payment"
            InputTarget.Reason, "reason"
        ]

    let token target =
        all |> List.find (fst >> (=) target) |> snd

[<RequireQualifiedAccess>]
type TextViolation =
    | NonBlankRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TooLong of maximumCharacters: int
    | ControlCharacters
    | FormattingCharacter
    | ReferenceFormat
    | CountryFormat

[<RequireQualifiedAccess>]
type DateViolation =
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate

[<RequireQualifiedAccess>]
type AmountViolation =
    | DecimalFormat of maximumIntegerDigits: int * maximumFractionalDigits: int
    | CurrencyFormat

[<RequireQualifiedAccess>]
type ActViolation =
    | EmptyOperationId
    | ExpectedRevisionOutOfRange

[<RequireQualifiedAccess>]
type CorrectionViolation =
    | PaymentClearRequired
    | PaymentAcknowledgementRequired

/// Closed reason with only typed constraint limits. Authored content is never a diagnostic argument.
[<RequireQualifiedAccess>]
type InputViolation =
    | Text of TextViolation
    | Date of DateViolation
    | Amount of AmountViolation
    | Act of ActViolation
    | Correction of CorrectionViolation
