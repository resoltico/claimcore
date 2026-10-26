namespace ClaimCore.Domain

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions

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

module private ScalarText =
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
                }
            MaximumCharacters = maximum
        }

[<Struct>]
type CalendarDate =
    private
    | CalendarDate of DateOnly

    member this.Value = let (CalendarDate value) = this in value

module CalendarDate =
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

[<Struct>]
type CountryCode = private CountryCode of string

module CountryCode =
    [<Literal>]
    let Characters = 2

    let Grammar = "[A-Z]{" + Characters.ToString(CultureInfo.InvariantCulture) + "}"
    let descriptor = ScalarText.descriptor (Some Grammar) None
    let format (CountryCode value) = value

    let parse value =
        ScalarText.token Grammar Violation.CountryFormat value |> Result.map CountryCode

    let admit (value: CountryCode) : Result<CountryCode, Violation> = Ok value

[<Struct>]
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

[<Struct>]
type Reason = private Reason of PartyName

module Reason =
    let descriptor = PartyName.descriptor
    let format (Reason value) = PartyName.format value

    let parse value =
        PartyName.parse value |> Result.map Reason

    let admit (Reason value) =
        PartyName.admit value |> Result.map Reason

[<Struct>]
type Money =
    private
    | Money of decimal * string

    member this.Value = let (Money(value, _)) = this in value
    member this.Currency = let (Money(_, currency)) = this in currency

type MoneyDescriptor =
    {
        Amount: AmountScalarRule
        Currency: CurrencyScalarRule
    }

[<RequireQualifiedAccess>]
type MoneyViolation =
    | Amount of Violation
    | Currency of Violation

module Money =
    [<Literal>]
    let MaximumIntegerDigits = 18

    [<Literal>]
    let MaximumFractionalDigits = 4

    [<Literal>]
    let CurrencyCharacters = 3

    let private amountGrammar =
        "(0|[1-9][0-9]{0,"
        + (MaximumIntegerDigits - 1).ToString(CultureInfo.InvariantCulture)
        + "})(\\.[0-9]{1,"
        + MaximumFractionalDigits.ToString(CultureInfo.InvariantCulture)
        + "})?"

    let private currencyGrammar =
        "[A-Z]{" + CurrencyCharacters.ToString(CultureInfo.InvariantCulture) + "}"

    let descriptor =
        {
            Amount =
                {
                    Text = ScalarText.descriptor (Some amountGrammar) None
                    Grammar = amountGrammar
                    MaximumIntegerDigits = MaximumIntegerDigits
                    MaximumFractionalDigits = MaximumFractionalDigits
                }
            Currency =
                {
                    Text = ScalarText.descriptor (Some currencyGrammar) None
                    Grammar = currencyGrammar
                    ExactCharacters = CurrencyCharacters
                }
        }

    let private amount value =
        if String.IsNullOrEmpty value || not (ScalarText.matches amountGrammar value) then
            None
        else
            match
                Decimal.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture
                )
            with
            | true, parsed -> Some parsed
            | false, _ -> None

    let private amountText (value: decimal) =
        value.ToString("0.####", CultureInfo.InvariantCulture)

    let format (Money(value, _)) = amountText value
    let currency (Money(_, value)) = value

    let canonicalAmount value =
        amount value |> Option.map amountText |> Option.defaultValue value

    let parse rawAmount rawCurrency =
        // Both permanent text checks precede either grammar, preserving diagnostic order.
        ScalarText.parse None 0 rawAmount
        |> Result.mapError MoneyViolation.Amount
        |> Result.bind (fun value ->
            ScalarText.parse None 0 rawCurrency
            |> Result.mapError MoneyViolation.Currency
            |> Result.bind (fun currency ->
                match amount value with
                | None ->
                    Error(
                        MoneyViolation.Amount(
                            Violation.DecimalFormat(MaximumIntegerDigits, MaximumFractionalDigits)
                        )
                    )
                | Some _ when not (ScalarText.matches currencyGrammar currency) ->
                    Error(MoneyViolation.Currency Violation.CurrencyFormat)
                | Some value -> Ok(Money(value, currency))))

    let admit (value: Money) : Result<Money, Violation> = Ok value

[<Struct>]
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

    let parse value =
        ScalarText.token Grammar Violation.ReferenceFormat value
        |> Result.map (fun value -> CaseReference(value, key value))

    let admit (value: CaseReference) : Result<CaseReference, Violation> = Ok value

    let validate value =
        parse value
        |> Result.map ignore
        |> Result.mapError (fun violation ->
            Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation))
