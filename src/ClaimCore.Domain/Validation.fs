namespace ClaimCore.Domain

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open ClaimCore.Domain.ResultFlow

/// Internal validated values. The public field schema does not expose internal progress variants.
module internal Validation =
    type Amount = { Value: decimal; Currency: string }

    type Facts =
        {
            IncidentDate: DateOnly
            NotificationDate: DateOnly
            Country: string
            Claimant: string
            Insurer: string
            Claimed: Amount
        }

    type Decision = { Date: DateOnly; Payable: Amount }

    let invalid field violation =
        Error(DomainError.InvalidInput(field, violation))

    let invalidCommand field violation =
        invalid field (InputViolation.Command violation)

    let invalidCorrection field violation =
        invalid field (InputViolation.Correction violation)

    let private validUnicode (value: string) =
        try
            UTF8Encoding(false, true).GetByteCount(value) |> ignore
            true
        with :? EncoderFallbackException ->
            false

    let private matches (grammar: string) (value: string) =
        Regex(@"\A" + grammar + @"\z", RegexOptions.CultureInvariant).IsMatch(value)

    let text field (value: string) =
        let constraints =
            FieldDefinitions.scalar (InputTargets.token field)
            |> ScalarRules.textConstraints

        if constraints.RequiresNonBlank && String.IsNullOrWhiteSpace(value) then
            invalid field (InputViolation.Text TextViolation.NonBlankRequired)
        elif constraints.RequiresWellFormedUnicode && not (validUnicode value) then
            invalid field (InputViolation.Text TextViolation.MalformedUnicode)
        elif constraints.RejectsSurroundingWhitespace && value <> value.Trim() then
            invalid field (InputViolation.Text TextViolation.SurroundingWhitespace)
        elif (value.EnumerateRunes() |> Seq.length) < constraints.MinimumCharacters then
            invalid
                field
                (InputViolation.Text(TextViolation.TooShort constraints.MinimumCharacters))
        elif (value.EnumerateRunes() |> Seq.length) > constraints.MaximumCharacters then
            invalid field (InputViolation.Text(TextViolation.TooLong constraints.MaximumCharacters))
        elif constraints.RejectsControlCharacters && (value |> Seq.exists Char.IsControl) then
            invalid field (InputViolation.Text TextViolation.ControlCharacters)
        else
            Ok value

    let date field (value: string) =
        match FieldDefinitions.scalar (InputTargets.token field) with
        | ScalarRule.CalendarDate constraints ->
            match
                DateOnly.TryParseExact(
                    value,
                    constraints.ExactFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
            with
            | true, parsed when
                value = parsed.ToString(constraints.ExactFormat, CultureInfo.InvariantCulture)
                && constraints.Minimum <= parsed
                && parsed <= constraints.Maximum
                ->
                Ok parsed
            | _ -> invalid field (InputViolation.Date DateViolation.CalendarDateRequired)
        | _ -> invalidArg "field" "The field is not a declared calendar-date field."

    let onOrBefore field (first: DateOnly) (last: DateOnly) =
        if first <= last then
            Ok()
        else
            invalid field (InputViolation.Date DateViolation.ChronologicalOrder)

    let notFuture field today value =
        if value <= today then
            Ok()
        else
            invalid field (InputViolation.Date DateViolation.FutureDate)

    let amount amountField currencyField rawAmount rawCurrency =
        result {
            let! valueText = text amountField rawAmount
            let! currency = text currencyField rawCurrency

            match
                FieldDefinitions.scalar (InputTargets.token amountField),
                FieldDefinitions.scalar (InputTargets.token currencyField)
            with
            | ScalarRule.Amount amountRule, ScalarRule.Currency currencyRule ->
                if not (matches amountRule.Grammar valueText) then
                    return!
                        invalid
                            amountField
                            (InputViolation.Amount(
                                AmountViolation.DecimalFormat(
                                    amountRule.MaximumIntegerDigits,
                                    amountRule.MaximumFractionalDigits
                                )
                            ))
                elif not (matches currencyRule.Grammar currency) then
                    return!
                        invalid currencyField (InputViolation.Amount AmountViolation.CurrencyFormat)
                else
                    match
                        Decimal.TryParse(
                            valueText,
                            NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture
                        )
                    with
                    | true, value -> return { Value = value; Currency = currency }
                    | _ ->
                        return!
                            invalid
                                amountField
                                (InputViolation.Amount AmountViolation.NotRepresentable)
            | _ ->
                return
                    invalidArg
                        "amountField"
                        "The amount and currency fields have incompatible scalar rules."
        }

    let registration (input: RegistrationInput) =
        result {
            let! incident = date InputTarget.IncidentDate input.IncidentDate

            let! notification =
                date InputTarget.IncidentNotificationDate input.IncidentNotificationDate

            do! onOrBefore InputTarget.IncidentNotificationDate incident notification

            let! country = text InputTarget.IncidentCountry input.IncidentCountry

            let! claimant = text InputTarget.ClaimantName input.ClaimantName

            let! insurer = text InputTarget.InsurerName input.InsurerName

            let! claimed =
                amount
                    InputTarget.ClaimedAmount
                    InputTarget.ClaimedCurrency
                    input.ClaimedAmount
                    input.ClaimedCurrency

            return
                {
                    IncidentDate = incident
                    NotificationDate = notification
                    Country = country
                    Claimant = claimant
                    Insurer = insurer
                    Claimed = claimed
                }
        }

    let decision (input: DecisionInput) =
        result {
            let! decisionDate = date InputTarget.PaymentDecisionDate input.PaymentDecisionDate

            let! payable =
                amount
                    InputTarget.PayableAmount
                    InputTarget.PayableCurrency
                    input.PayableAmount
                    input.PayableCurrency

            return
                {
                    Date = decisionDate
                    Payable = payable
                }
        }

    let amountText (value: Amount) =
        value.Value.ToString("0.####", CultureInfo.InvariantCulture)

    let dateText (value: DateOnly) =
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
