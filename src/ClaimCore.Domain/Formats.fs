namespace ClaimCore.Domain


/// Permanent scalar syntax and retained byte bounds. Admission policy may be stricter.
module Formats =
    [<Literal>]
    let MaximumFreeTextBytes = PartyName.MaximumBytes

    [<Literal>]
    let MaximumPayloadBytes = 32768

    [<Literal>]
    let ReferenceMaximumCharacters = CaseReference.MaximumCharacters

    [<Literal>]
    let AmountIntegerDigits = Money.MaximumIntegerDigits

    [<Literal>]
    let AmountFractionalDigits = Money.MaximumFractionalDigits

    [<Literal>]
    let CountryCharacters = CountryCode.Characters

    [<Literal>]
    let CurrencyCharacters = Money.CurrencyCharacters

    [<Literal>]
    let CalendarDateFormat = CalendarDate.ExactFormat

    let reference = CaseReference.Grammar
    let country = CountryCode.Grammar
    let currency = Money.descriptor.Currency.Grammar
    let amount = Money.descriptor.Amount.Grammar
    let amountMaximumCharacters = AmountIntegerDigits + 1 + AmountFractionalDigits
    let dateCharacters = CalendarDateFormat.Length
    let date = CalendarDate.Grammar
    let anchored grammar = "^(?:" + grammar + ")(?![\\s\\S])"
