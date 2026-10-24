namespace ClaimCore.Domain

open ClaimCore.Contract

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
        Error(Refusal.InvalidInput(field, violation))

    let private matches (grammar: string) (value: string) =
        Regex.IsMatch(
            value,
            @"\A" + grammar + @"\z",
            RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
        )

    let private textGrammar field =
        match field with
        | (InputTarget.Field BusinessField.CaseReference)
        | (InputTarget.Field BusinessField.IncidentCountry) ->
            FieldDefinitions.scalar field |> ScalarRules.textConstraints |> _.Format.Grammar
        | _ -> None

    /// Format checks apply to requests and retained records alike; policy never runs here.
    let text field (value: string) =
        let failure violation = invalid field (violation)

        if String.IsNullOrEmpty value then
            failure Violation.TextRequired
        elif not (UnicodeText.isPersisted value) then
            failure Violation.MalformedUnicode
        elif
            (field = (InputTarget.Field BusinessField.ClaimantName)
             || field = (InputTarget.Field BusinessField.InsurerName)
             || field = InputTarget.Reason)
            && Encoding.UTF8.GetByteCount value > TextFormat.MaximumFreeTextBytes
        then
            // More than 1,024 UTF-8 bytes needs at least 257 scalars, beyond current input admission.
            failure (Violation.TooLong TextAdmission.MaximumCharacters)
        elif textGrammar field |> Option.exists (fun pattern -> not (matches pattern value)) then
            failure (
                if field = (InputTarget.Field BusinessField.CaseReference) then
                    Violation.ReferenceFormat
                else
                    Violation.CountryFormat
            )
        else
            Ok value

    let private formatting scalar =
        let ranges =
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

        List.contains scalar [ 0x00AD; 0x061C; 0x200B; 0x3164; 0xFEFF; 0xFFA0 ]
        || List.exists (fun (first, last) -> scalar >= first && scalar <= last) ranges

    /// Called only after format validation, when a value is newly asserted.
    let admission field (value: string) =
        let constraints = FieldDefinitions.scalar field |> ScalarRules.textConstraints

        let failure violation = invalid field (violation)

        match constraints.Policy with
        | None -> Ok()
        | Some policy ->
            if policy.RequiresNonBlank && String.IsNullOrWhiteSpace value then
                failure Violation.TextRequired
            elif policy.RejectsSurroundingWhitespace && value <> value.Trim() then
                failure Violation.SurroundingWhitespace
            elif (value.EnumerateRunes() |> Seq.length) > policy.MaximumCharacters then
                failure (Violation.TooLong policy.MaximumCharacters)
            elif policy.RejectsControlCharacters && (value |> Seq.exists Char.IsControl) then
                failure Violation.ControlCharacters
            elif
                policy.RejectsForbiddenInvisibleCharacters
                && (value.EnumerateRunes() |> Seq.exists (fun rune -> formatting rune.Value))
            then
                failure Violation.FormattingCharacter
            else
                Ok()

    let assertedText previous (facts: Facts) =
        let changed field before after =
            if before = Some after then Ok() else admission field after

        result {
            do!
                changed
                    (InputTarget.Field BusinessField.ClaimantName)
                    (previous |> Option.map _.Claimant)
                    facts.Claimant

            do!
                changed
                    (InputTarget.Field BusinessField.InsurerName)
                    (previous |> Option.map _.Insurer)
                    facts.Insurer
        }

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
            | _ -> invalid field (Violation.CalendarDateRequired)
        | _ -> invalidArg "field" "The field is not a declared calendar-date field."

    let onOrBefore field (first: DateOnly) (last: DateOnly) =
        if first <= last then
            Ok()
        else
            invalid field (Violation.ChronologicalOrder)

    let notFuture field today value =
        if value <= today then
            Ok()
        else
            invalid field (Violation.FutureDate)

    let amount amountField currencyField rawAmount rawCurrency =
        result {
            let! valueText = text amountField rawAmount
            let! currency = text currencyField rawCurrency

            match FieldDefinitions.scalar amountField, FieldDefinitions.scalar currencyField with
            | ScalarRule.Amount amountRule, ScalarRule.Currency currencyRule ->
                if not (matches amountRule.Grammar valueText) then
                    return!
                        invalid
                            amountField
                            ((Violation.DecimalFormat(
                                amountRule.MaximumIntegerDigits,
                                amountRule.MaximumFractionalDigits
                            )))
                elif not (matches currencyRule.Grammar currency) then
                    return! invalid currencyField (Violation.CurrencyFormat)
                else
                    return
                        {
                            Value =
                                Decimal.Parse(
                                    valueText,
                                    NumberStyles.AllowDecimalPoint,
                                    CultureInfo.InvariantCulture
                                )
                            Currency = currency
                        }
            | _ ->
                return
                    invalidArg
                        "amountField"
                        "The amount and currency fields have incompatible scalar rules."
        }

    let registration (input: Registration) =
        result {
            let! incident =
                date (InputTarget.Field BusinessField.IncidentDate) input.IncidentDate

            let! notification =
                date
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    input.IncidentNotificationDate

            do!
                onOrBefore
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    incident
                    notification

            let! country =
                text (InputTarget.Field BusinessField.IncidentCountry) input.IncidentCountry

            let! claimant =
                text (InputTarget.Field BusinessField.ClaimantName) input.ClaimantName

            let! insurer = text (InputTarget.Field BusinessField.InsurerName) input.InsurerName

            let! claimed =
                amount
                    (InputTarget.Field BusinessField.ClaimedAmount)
                    (InputTarget.Field BusinessField.ClaimedCurrency)
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

    let decision (input: ClaimCore.Contract.Decision) =
        result {
            let! decisionDate =
                date (InputTarget.Field BusinessField.PaymentDecisionDate) input.PaymentDecisionDate

            let! payable =
                amount
                    (InputTarget.Field BusinessField.PayableAmount)
                    (InputTarget.Field BusinessField.PayableCurrency)
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
