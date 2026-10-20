namespace ClaimCore.Domain

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
