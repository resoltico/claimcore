namespace ClaimCore.Domain

open ClaimCore.Contract

open System

/// Permanent text representability and syntax. Policy is never a generic schema assertion.
type TextFormatConstraints =
    {
        MinimumCharacters: int
        Grammar: string option
    }

type TextAdmissionPolicy =
    {
        MaximumCharacters: int
        RequiresNonBlank: bool
        RejectsSurroundingWhitespace: bool
        RejectsControlCharacters: bool
        RejectsForbiddenInvisibleCharacters: bool
    }

type ScalarTextConstraints =
    {
        Format: TextFormatConstraints
        Policy: TextAdmissionPolicy option
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

/// Catalog supplies closed status vocabulary; Domain owns transitions.
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
    | CurrentField of field: BusinessField

/// Ordered authored scalar metadata. The immutable case target is never an input field.
type FieldInputDefinition =
    {
        FieldName: InputTarget
        Prefill: PrefillSource
    }

/// An explicit operator choice for one correction group. The tag is part of the request shape;
/// it is not inferred from omitted or nullable scalar fields.
/// One required grouped correction input. ReplaceFields are required only for REPLACE. KEEP reads
/// the authoritative accepted value and CLEAR deliberately removes a value that is present.
type CorrectionGroupDefinition =
    {
        Name: CorrectionGroup
        LabelId: string
        MeaningId: string
        Actions: CorrectionGroupAction list
        ReplaceFields: FieldInputDefinition list
    }

/// Commands either collect one ordered scalar object or explicitly collect required correction
/// groups. This is the semantic source for every adapter; it avoids optional flat correction fields.
[<RequireQualifiedAccess>]
type CommandInputShape =
    | Fields of FieldInputDefinition list
    | CorrectionGroups of CorrectionGroupDefinition list

module ScalarRules =
    let textConstraints rule =
        match rule with
        | ScalarRule.Text constraints -> constraints
        | ScalarRule.Amount amount -> amount.Text
        | ScalarRule.Currency currency -> currency.Text
        | ScalarRule.CalendarDate _
        | ScalarRule.CaseStatus _ -> invalidArg "rule" "The scalar rule is not text-like."
