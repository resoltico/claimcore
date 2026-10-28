namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type Violation =
    | TextRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TooLong of maximumCharacters: int
    | ControlCharacters
    | FormattingCharacter
    | ReferenceFormat
    | CountryFormat
    | PrincipalFormat
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate
    | DecimalFormat of integerDigits: int * fractionDigits: int
    | CurrencyFormat
    | OperationIdRequired
    | ExpectedRevisionRange
    | PageLimitRange of maximumPageSize: int
    | PaymentClearRequired
    | PaymentAcknowledgementRequired
