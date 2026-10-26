namespace ClaimCore.Contract

/// Shared scalar rules referenced by the authoritative field rows.
module FieldRules =
    let token grammar : ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = Some grammar
                }
            MaximumCharacters = None
        }

    let freeText: ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = None
                }
            MaximumCharacters = Some Admission.MaximumCharacters
        }

    let calendarDate: CalendarDateScalarRule =
        {
            ExactFormat = Formats.CalendarDateFormat
            Minimum = System.DateOnly.MinValue
            Maximum = System.DateOnly.MaxValue
        }

    let amount: AmountScalarRule =
        {
            Text = token Formats.amount
            Grammar = Formats.amount
            MaximumIntegerDigits = Formats.AmountIntegerDigits
            MaximumFractionalDigits = Formats.AmountFractionalDigits
        }

    let currency: CurrencyScalarRule =
        {
            Text = token Formats.currency
            Grammar = Formats.currency
            ExactCharacters = Formats.CurrencyCharacters
        }
