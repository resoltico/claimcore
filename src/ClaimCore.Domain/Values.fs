namespace ClaimCore.Domain

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions

/// The two case statuses requested by the operator. Payment never changes this implicitly.
[<RequireQualifiedAccess>]
type CaseStatus =
    | Opened
    | Closed

/// Permanent text representability and syntax. Policy is never a generic schema assertion.
type TextFormatConstraints =
    {
        MinimumCharacters: int
        Grammar: string option
        UnknownCode: string option
    }

type ScalarTextConstraints =
    {
        Format: TextFormatConstraints
        MaximumCharacters: int option
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
    | PlannedDate of CalendarDateScalarRule
    | Text of ScalarTextConstraints
    | Amount of AmountScalarRule
    | Currency of CurrencyScalarRule
    | CaseStatus of CaseStatusScalarRule

/// Permanent Unicode interchange rules are shared by scalar and JSON boundaries.
module UnicodeText =
    let isInterchange (value: string) =
        if obj.ReferenceEquals(value, null) then
            false
        else
            try
                UTF8Encoding(false, true).GetByteCount(value) |> ignore

                value.EnumerateRunes()
                |> Seq.forall (fun rune ->
                    not (rune.Value >= 0xFDD0 && rune.Value <= 0xFDEF)
                    && (rune.Value &&& 0xFFFE) <> 0xFFFE)
            with :? EncoderFallbackException ->
                false

    /// PostgreSQL text additionally cannot represent U+0000. Generic JSON may escape it.
    let isPersisted (value: string) =
        isInterchange value && not (value.Contains '\u0000')

module internal ScalarText =
    let matches grammar (value: string) =
        Regex.IsMatch(
            value,
            "\\A" + grammar + "\\z",
            RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
        )

    let parse maximumBytes maximumCharacters (value: string) =
        if String.IsNullOrEmpty value then
            Error Violation.TextRequired
        elif not (UnicodeText.isPersisted value) then
            Error Violation.MalformedUnicode
        elif
            maximumBytes
            |> Option.exists (fun maximum -> Encoding.UTF8.GetByteCount value > maximum)
        then
            Error(Violation.TooLong maximumCharacters)
        else
            Ok value

    let token grammar violation value =
        parse None 0 value
        |> Result.bind (fun text -> if matches grammar text then Ok text else Error violation)

    let private formatting scalar =
        List.contains scalar [ 0x00AD; 0x061C; 0x200B; 0x3164; 0xFEFF; 0xFFA0 ]
        || List.exists
            (fun (first, last) -> scalar >= first && scalar <= last)
            [
                0x115F, 0x1160
                0x17B4, 0x17B5
                0x200E, 0x200F
                0x2028, 0x202E
                0x2060, 0x2064
                0x2066, 0x2069
                0xFFF9, 0xFFFB
                0xE0000, 0xE007F
            ]

    let admit maximum (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error Violation.TextRequired
        elif value <> value.Trim() then
            Error Violation.SurroundingWhitespace
        elif (value.EnumerateRunes() |> Seq.length) > maximum then
            Error(Violation.TooLong maximum)
        elif value |> Seq.exists Char.IsControl then
            Error Violation.ControlCharacters
        elif value.EnumerateRunes() |> Seq.exists (fun rune -> formatting rune.Value) then
            Error Violation.FormattingCharacter
        else
            Ok value

    let descriptor grammar maximum : ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = grammar
                    UnknownCode = None
                }
            MaximumCharacters = maximum
        }

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type CalendarDate = private CalendarDate of DateOnly


module CalendarDate =
    let value (CalendarDate date) = date

    [<Literal>]
    let ExactFormat = "yyyy-MM-dd"

    let Grammar = "(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}"

    let descriptor: CalendarDateScalarRule =
        {
            ExactFormat = ExactFormat
            Minimum = DateOnly.MinValue
            Maximum = DateOnly.MaxValue
        }

    let format (CalendarDate value) =
        value.ToString(ExactFormat, CultureInfo.InvariantCulture)

    let ofDate value = CalendarDate value

    let parse value =
        match
            DateOnly.TryParseExact(
                value,
                ExactFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
        with
        | true, date when format (CalendarDate date) = value -> Ok(CalendarDate date)
        | _ -> Error Violation.CalendarDateRequired

    let admit today (CalendarDate date as value) =
        if date <= today then
            Ok value
        else
            Error Violation.FutureDate

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type CountryCode = private CountryCode of string

module CountryCode =
    [<Literal>]
    let Characters = 2

    let Grammar = "[A-Z]{" + Characters.ToString(CultureInfo.InvariantCulture) + "}"
    let unknown = CountryCode "ZZ"
    let format (CountryCode value) = value

    let descriptor =
        let value = ScalarText.descriptor (Some Grammar) None

        { value with
            Format =
                { value.Format with
                    UnknownCode = Some(format unknown)
                }
        }

    let parse value =
        ScalarText.token Grammar Violation.CountryFormat value |> Result.map CountryCode

    let admit (value: CountryCode) = Ok value

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type PartyName = private PartyName of string

module PartyName =
    [<Literal>]
    let MaximumBytes = 1024

    [<Literal>]
    let MaximumCharacters = 200

    let descriptor = ScalarText.descriptor None (Some MaximumCharacters)
    let format (PartyName value) = value

    let parse value =
        ScalarText.parse (Some MaximumBytes) MaximumCharacters value
        |> Result.map PartyName

    let admit (PartyName value) =
        ScalarText.admit MaximumCharacters value |> Result.map PartyName

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type Reason = private Reason of PartyName

module Reason =
    let descriptor = PartyName.descriptor
    let format (Reason value) = PartyName.format value

    let parse value =
        PartyName.parse value |> Result.map Reason

    let admit (Reason value) =
        PartyName.admit value |> Result.map Reason

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type CaseReference = private CaseReference of authored: string * key: string

module CaseReference =
    [<Literal>]
    let MaximumCharacters = 80

    let Grammar =
        "[!-~]{1," + MaximumCharacters.ToString(CultureInfo.InvariantCulture) + "}"

    let descriptor = ScalarText.descriptor (Some Grammar) None

    /// ASCII identity folding never consults culture or Unicode normalization.
    let key (reference: string) =
        reference
        |> Seq.map (fun c -> if c >= 'a' && c <= 'z' then char (int c - 32) else c)
        |> Seq.toArray
        |> String

    let format (CaseReference(authored, _)) = authored
    let identity (CaseReference(_, key)) = key
    let canonical (CaseReference(_, key)) = CaseReference(key, key)

    let parse value =
        ScalarText.token Grammar Violation.ReferenceFormat value
        |> Result.map (fun value -> CaseReference(value, key value))
