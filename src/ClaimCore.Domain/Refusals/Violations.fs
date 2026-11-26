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
    | CustomReferenceNotAllowed
    | CountryFormat
    | PrincipalFormat
    | CaseReferencePolicyFormat
    | CaseIdRequired
    | CaseSelectionInvalid
    | ActorIdRequired
    | ReminderIdRequired
    | NoteIdRequired
    | AttachmentIdRequired
    | AttachmentNameInvalid
    | AttachmentTypeRefused
    | AttachmentLengthInvalid
    | AttachmentDigestInvalid
    | AttachmentPayloadRequired
    | AttachmentPayloadMismatch
    | PartyIdRequired
    | PartyRoleRequired
    | PartyContentRequired
    | PartyCountRange
    | LinkIdRequired
    | EmailAddressInvalid
    | UpdateRequired
    | CalendarDateRequired
    | FutureDate
    | DecimalFormat of integerDigits: int * fractionDigits: int
    | CurrencyFormat
    | OperationIdRequired
    | ExpectedRevisionRange
    | PageLimitRange of maximumPageSize: int
    | GrantCountRange of maximumGrants: int
    | DuplicateGrant
    | PaymentCountLimit
    | PaymentAmountPositive
    | PaymentDirectionFormat
    | PaymentTotalRange
    | PayeeUnavailable
    | ClosureOutcomeFormat
    | PaymentIdRequired
