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

    let invalid field message =
        Error(DomainError.InvalidInput(field, message))

    let private validUnicode (value: string) =
        try
            UTF8Encoding(false, true).GetByteCount(value) |> ignore
            true
        with :? EncoderFallbackException ->
            false

    let private matches (grammar: string) (value: string) =
        Regex(@"\A" + grammar + @"\z", RegexOptions.CultureInvariant).IsMatch(value)

    let private amountFormatMessage (rule: AmountScalarRule) =
        $"Use non-negative decimal text: up to {rule.MaximumIntegerDigits} integer digits and {rule.MaximumFractionalDigits} fractional digits; no sign or exponent."

    let text field (value: string) =
        let constraints = FieldDefinitions.scalar field |> ScalarRules.textConstraints

        if constraints.RequiresNonBlank && String.IsNullOrWhiteSpace(value) then
            invalid field "A non-blank value is required."
        elif constraints.RequiresWellFormedUnicode && not (validUnicode value) then
            invalid field "Malformed Unicode is not accepted."
        elif constraints.RejectsSurroundingWhitespace && value <> value.Trim() then
            invalid field "Leading or trailing whitespace is not accepted."
        elif (value.EnumerateRunes() |> Seq.length) < constraints.MinimumCharacters then
            invalid
                field
                $"The value must contain at least {constraints.MinimumCharacters} Unicode characters."
        elif (value.EnumerateRunes() |> Seq.length) > constraints.MaximumCharacters then
            invalid
                field
                $"The value must not exceed {constraints.MaximumCharacters} Unicode characters."
        elif constraints.RejectsControlCharacters && (value |> Seq.exists Char.IsControl) then
            invalid field "Control characters are not accepted."
        else
            Ok value

    let date field (value: string) =
        match FieldDefinitions.scalar field with
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
            | _ -> invalid field "Use one valid calendar date in YYYY-MM-DD format."
        | _ -> invalidArg "field" "The field is not a declared calendar-date field."

    let onOrBefore field (first: DateOnly) (last: DateOnly) =
        if first <= last then
            Ok()
        else
            invalid field "The dates are in an invalid chronological order."

    let notFuture field today value =
        if value <= today then
            Ok()
        else
            invalid field "A future date cannot record an event that has already occurred."

    let amount amountField currencyField rawAmount rawCurrency =
        result {
            let! valueText = text amountField rawAmount
            let! currency = text currencyField rawCurrency

            match FieldDefinitions.scalar amountField, FieldDefinitions.scalar currencyField with
            | ScalarRule.Amount amountRule, ScalarRule.Currency currencyRule ->
                if not (matches amountRule.Grammar valueText) then
                    return! invalid amountField (amountFormatMessage amountRule)
                elif not (matches currencyRule.Grammar currency) then
                    return!
                        invalid currencyField "Use a three-letter uppercase currency identifier."
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
                            invalid amountField "The decimal amount cannot be represented exactly."
            | _ ->
                return
                    invalidArg
                        "amountField"
                        "The amount and currency fields have incompatible scalar rules."
        }

    let registration (input: RegistrationInput) =
        result {
            let! incident = date "incidentDate" input.IncidentDate
            let! notification = date "incidentNotificationDate" input.IncidentNotificationDate
            do! onOrBefore "incidentNotificationDate" incident notification

            let! country = text "incidentCountry" input.IncidentCountry

            let! claimant = text "claimantName" input.ClaimantName

            let! insurer = text "insurerName" input.InsurerName

            let! claimed =
                amount "claimedAmount" "claimedCurrency" input.ClaimedAmount input.ClaimedCurrency

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
            let! decisionDate = date "paymentDecisionDate" input.PaymentDecisionDate

            let! payable =
                amount "payableAmount" "payableCurrency" input.PayableAmount input.PayableCurrency

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
