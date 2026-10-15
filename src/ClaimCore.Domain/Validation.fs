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
        Error(CaseRefusal.InvalidInput(field, violation))

    let invalidAct field violation =
        invalid field (InputViolation.Act violation)

    let invalidCorrection field violation =
        invalid field (InputViolation.Correction violation)

    let private matches (grammar: string) (value: string) =
        Regex.IsMatch(
            value,
            @"\A" + grammar + @"\z",
            RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
        )

    let private textGrammar field =
        match field with
        | InputTarget.CaseReference
        | InputTarget.IncidentCountry ->
            FieldDefinitions.scalar (InputTargets.token field)
            |> ScalarRules.textConstraints
            |> _.Format.Grammar
        | _ -> None

    /// Format checks apply to requests and retained records alike; policy never runs here.
    let text field (value: string) =
        let failure violation =
            invalid field (InputViolation.Text violation)

        if String.IsNullOrEmpty value then
            failure TextViolation.NonBlankRequired
        elif not (UnicodeText.isPersisted value) then
            failure TextViolation.MalformedUnicode
        elif textGrammar field |> Option.exists (fun pattern -> not (matches pattern value)) then
            failure (
                if field = InputTarget.CaseReference then
                    TextViolation.ReferenceFormat
                else
                    TextViolation.CountryFormat
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
        let constraints =
            FieldDefinitions.scalar (InputTargets.token field)
            |> ScalarRules.textConstraints

        let failure violation =
            invalid field (InputViolation.Text violation)

        match constraints.Policy with
        | None -> Ok()
        | Some policy ->
            if policy.RequiresNonBlank && String.IsNullOrWhiteSpace value then
                failure TextViolation.NonBlankRequired
            elif policy.RejectsSurroundingWhitespace && value <> value.Trim() then
                failure TextViolation.SurroundingWhitespace
            elif (value.EnumerateRunes() |> Seq.length) > policy.MaximumCharacters then
                failure (TextViolation.TooLong policy.MaximumCharacters)
            elif policy.RejectsControlCharacters && (value |> Seq.exists Char.IsControl) then
                failure TextViolation.ControlCharacters
            elif
                policy.RejectsFormattingCharacters
                && (value.EnumerateRunes() |> Seq.exists (fun rune -> formatting rune.Value))
            then
                failure TextViolation.FormattingCharacter
            else
                Ok()

    let assertedText previous (facts: Facts) =
        let changed field before after =
            if before = Some after then Ok() else admission field after

        result {
            do! changed InputTarget.ClaimantName (previous |> Option.map _.Claimant) facts.Claimant
            do! changed InputTarget.InsurerName (previous |> Option.map _.Insurer) facts.Insurer
        }

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
