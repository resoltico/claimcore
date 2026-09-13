namespace ClaimCore.Domain

open System

/// Validation constraints shared by text-like scalar rules.
type ScalarTextConstraints =
    {
        MinimumCharacters: int
        MaximumCharacters: int
        RequiresNonBlank: bool
        RejectsSurroundingWhitespace: bool
        RejectsControlCharacters: bool
        RequiresWellFormedUnicode: bool
    }

/// Exact Gregorian calendar-date input rules.
type CalendarDateScalarRule =
    {
        ExactFormat: string
        Minimum: DateOnly
        Maximum: DateOnly
    }

/// Exact non-negative decimal-text input rules.
type AmountScalarRule =
    {
        Text: ScalarTextConstraints
        Grammar: string
        MaximumIntegerDigits: int
        MaximumFractionalDigits: int
    }

/// Exact uppercase ASCII currency-token input rules.
type CurrencyScalarRule =
    {
        Text: ScalarTextConstraints
        Grammar: string
        ExactCharacters: int
    }

/// Closed status vocabulary owned by the Domain.
type CaseStatusScalarRule = { AllowedValues: CaseStatus list }

/// Typed scalar rules are the Domain authority for adapter-visible field constraints.
[<RequireQualifiedAccess>]
type ScalarRule =
    | CalendarDate of CalendarDateScalarRule
    | Text of ScalarTextConstraints
    | Amount of AmountScalarRule
    | Currency of CurrencyScalarRule
    | CaseStatus of CaseStatusScalarRule

/// A command form may begin blank or reuse a current business-field value as a suggestion.
[<RequireQualifiedAccess>]
type PrefillSource =
    | Blank
    | CurrentField of fieldName: string

/// Ordered authored input metadata. The immutable case target is never an input field.
type CommandInputDefinition =
    {
        FieldName: string
        Prefill: PrefillSource
    }

/// Static, adapter-visible identifiers for executable Domain rules.
[<RequireQualifiedAccess>]
type DomainRuleCategory =
    | CrossField
    | Transition

type DomainRuleDefinition =
    {
        Identifier: string
        Category: DomainRuleCategory
        Meaning: string
    }

module ScalarRules =
    let textConstraints rule =
        match rule with
        | ScalarRule.Text constraints -> constraints
        | ScalarRule.Amount amount -> amount.Text
        | ScalarRule.Currency currency -> currency.Text
        | ScalarRule.CalendarDate _
        | ScalarRule.CaseStatus _ -> invalidArg "rule" "The scalar rule is not text-like."

module DomainRules =
    let private crossFieldRules =
        [
            {
                Identifier = "IMMUTABLE_CASE_REFERENCE"
                Category = DomainRuleCategory.CrossField
                Meaning = "The command target must equal the accepted case reference."
            }
            {
                Identifier = "EVENT_DATE_ORDER"
                Category = DomainRuleCategory.CrossField
                Meaning =
                    "Incident, notification, decision and payment dates must remain chronological."
            }
            {
                Identifier = "EVENT_DATE_NOT_FUTURE"
                Category = DomainRuleCategory.CrossField
                Meaning = "Newly recorded event dates cannot be after the effective business date."
            }
            {
                Identifier = "DECISION_TUPLE_COMPLETE"
                Category = DomainRuleCategory.CrossField
                Meaning =
                    "Decision date, payable amount and payable currency are present together or absent together."
            }
            {
                Identifier = "PAYMENT_REQUIRES_POSITIVE_DECISION"
                Category = DomainRuleCategory.CrossField
                Meaning = "A payment date requires a complete, positive decision."
            }
        ]

    let private admissionRules =
        [
            {
                Identifier = "OPEN_REQUIRES_ABSENT_CASE"
                Category = DomainRuleCategory.Transition
                Meaning =
                    "OPEN is the only command that creates a case and it requires no current case."
            }
            {
                Identifier = "OPEN_REQUIRES_ZERO_EXPECTED_REVISION"
                Category = DomainRuleCategory.Transition
                Meaning = "OPEN requires expected revision zero."
            }
            {
                Identifier = "MUTATION_REQUIRES_CURRENT_CASE"
                Category = DomainRuleCategory.Transition
                Meaning = "Every command other than OPEN requires an accepted current case."
            }
            {
                Identifier = "EXPECTED_REVISION_REVALIDATED"
                Category = DomainRuleCategory.Transition
                Meaning =
                    "Every change checks the authoritative accepted revision before transition."
            }
        ]

    let private transitionRules =
        [
            {
                Identifier = "CLOSED_CASE_REOPEN_FIRST"
                Category = DomainRuleCategory.Transition
                Meaning =
                    "A closed case permits only REOPEN; it must be reopened before other changes."
            }
            {
                Identifier = "AMENDMENT_REQUIRES_UNDECIDED_CASE"
                Category = DomainRuleCategory.Transition
                Meaning = "Registration facts may change only while the case has no decision."
            }
            {
                Identifier = "DECISION_REQUIRES_UNPAID_CASE"
                Category = DomainRuleCategory.Transition
                Meaning = "A decision may be added or replaced only before payment is recorded."
            }
            {
                Identifier = "WITHDRAWAL_REQUIRES_UNPAID_DECISION"
                Category = DomainRuleCategory.Transition
                Meaning = "Decision withdrawal requires an existing unpaid decision."
            }
            {
                Identifier = "PAYMENT_REQUIRES_UNPAID_DECISION"
                Category = DomainRuleCategory.Transition
                Meaning =
                    "Recording payment requires an existing unpaid decision and may occur once."
            }
            {
                Identifier = "CLEAR_PAYMENT_REQUIRES_RECORDED_PAYMENT"
                Category = DomainRuleCategory.Transition
                Meaning = "CLEAR_PAYMENT requires an existing recorded payment date."
            }
            {
                Identifier = "CLOSE_REQUIRES_OPEN_CASE"
                Category = DomainRuleCategory.Transition
                Meaning = "CLOSE applies only to an opened case."
            }
            {
                Identifier = "REOPEN_REQUIRES_CLOSED_CASE"
                Category = DomainRuleCategory.Transition
                Meaning = "REOPEN applies only to a closed case."
            }
        ]

    let all = crossFieldRules @ admissionRules @ transitionRules
