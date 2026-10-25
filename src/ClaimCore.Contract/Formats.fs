namespace ClaimCore.Contract

open System.Globalization

/// Permanent scalar syntax and retained byte bounds. Admission policy may be stricter.
module Formats =
    [<Literal>]
    let MaximumFreeTextBytes = 1024

    [<Literal>]
    let MaximumPayloadBytes = 32768

    [<Literal>]
    let ReferenceMaximumCharacters = 80

    [<Literal>]
    let AmountIntegerDigits = 18

    [<Literal>]
    let AmountFractionalDigits = 4

    [<Literal>]
    let CountryCharacters = 2

    [<Literal>]
    let CurrencyCharacters = 3

    [<Literal>]
    let CalendarDateFormat = "yyyy-MM-dd"

    [<Literal>]
    let InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"

    let private digits (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    let reference = "[!-~]{1," + digits ReferenceMaximumCharacters + "}"
    let country = "[A-Z]{" + digits CountryCharacters + "}"
    let currency = "[A-Z]{" + digits CurrencyCharacters + "}"

    let amount =
        "(0|[1-9][0-9]{0,"
        + digits (AmountIntegerDigits - 1)
        + "})(\\.[0-9]{1,"
        + digits AmountFractionalDigits
        + "})?"

    let amountMaximumCharacters = AmountIntegerDigits + 1 + AmountFractionalDigits
    let dateCharacters = CalendarDateFormat.Length
    let date = "(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}"
    let anchored grammar = "^(?:" + grammar + ")(?![\\s\\S])"
