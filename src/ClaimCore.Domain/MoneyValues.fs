namespace ClaimCore.Domain

open System
open System.Globalization

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type Amount = private Amount of decimal

module Amount =
    [<Literal>]
    let MaximumIntegerDigits = 18

    [<Literal>]
    let MaximumFractionalDigits = 4

    let Grammar =
        "(0|[1-9][0-9]{0,"
        + (MaximumIntegerDigits - 1).ToString(CultureInfo.InvariantCulture)
        + "})(\\.[0-9]{1,"
        + MaximumFractionalDigits.ToString(CultureInfo.InvariantCulture)
        + "})?"

    let descriptor =
        {
            Text = ScalarText.descriptor (Some Grammar) None
            Grammar = Grammar
            MaximumIntegerDigits = MaximumIntegerDigits
            MaximumFractionalDigits = MaximumFractionalDigits
        }

    let value (Amount value) = value

    let format (Amount value) =
        value.ToString("0.####", CultureInfo.InvariantCulture)

    let parse raw =
        ScalarText.parse None 0 raw
        |> Result.bind (fun text ->
            if not (ScalarText.matches Grammar text) then
                Error(Violation.DecimalFormat(MaximumIntegerDigits, MaximumFractionalDigits))
            else
                match
                    Decimal.TryParse(
                        text,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture
                    )
                with
                | true, value -> Ok(Amount value)
                | _ ->
                    Error(Violation.DecimalFormat(MaximumIntegerDigits, MaximumFractionalDigits)))

    let admit (value: Amount) = Ok value

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type Currency = private Currency of string

module Currency =
    [<Literal>]
    let Characters = 3

    let Grammar = "[A-Z]{" + Characters.ToString(CultureInfo.InvariantCulture) + "}"

    let descriptor =
        {
            Text = ScalarText.descriptor (Some Grammar) None
            Grammar = Grammar
            ExactCharacters = Characters
        }

    let format (Currency value) = value

    let parse raw =
        ScalarText.token Grammar Violation.CurrencyFormat raw |> Result.map Currency

    let admit (value: Currency) = Ok value

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type Money = private Money of Amount * Currency

type MoneyDescriptor =
    {
        Amount: AmountScalarRule
        Currency: CurrencyScalarRule
    }

module Money =
    let value (Money(amount, _)) = Amount.value amount
    let format (Money(amount, _)) = Amount.format amount
    let currency (Money(_, currency)) = Currency.format currency
    let amount (Money(amount, _)) = amount
    let currencyValue (Money(_, currency)) = currency
    let create amount currency = Money(amount, currency)
    let MaximumIntegerDigits = Amount.MaximumIntegerDigits
    let MaximumFractionalDigits = Amount.MaximumFractionalDigits
    let CurrencyCharacters = Currency.Characters

    let descriptor =
        {
            Amount = Amount.descriptor
            Currency = Currency.descriptor
        }
