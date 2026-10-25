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

    let private representable field freeText (value: string) =
        if String.IsNullOrEmpty value then
            invalid field Violation.TextRequired
        elif not (UnicodeText.isPersisted value) then
            invalid field Violation.MalformedUnicode
        elif freeText && Encoding.UTF8.GetByteCount value > Formats.MaximumFreeTextBytes then
            invalid field (Violation.TooLong TextAdmission.MaximumCharacters)
        else
            Ok value

    /// Permanent text syntax is selected only from text inputs.
    let text input (value: string) =
        let field = ScalarInputs.textTarget input
        let constraints = FieldDefinitions.text input

        representable field constraints.MaximumCharacters.IsSome value
        |> Result.bind (fun value ->
            if
                constraints.Format.Grammar
                |> Option.exists (fun pattern -> not (matches pattern value))
            then
                invalid
                    field
                    (match input with
                     | TextInput.CaseReference -> Violation.ReferenceFormat
                     | _ -> Violation.CountryFormat)
            else
                Ok value)

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
    let admission input (value: string) =
        let field = ScalarInputs.textTarget input

        match (FieldDefinitions.text input).MaximumCharacters with
        | None -> Ok()
        | Some maximum ->
            if String.IsNullOrWhiteSpace value then
                invalid field Violation.TextRequired
            elif value <> value.Trim() then
                invalid field Violation.SurroundingWhitespace
            elif (value.EnumerateRunes() |> Seq.length) > maximum then
                invalid field (Violation.TooLong maximum)
            elif value |> Seq.exists Char.IsControl then
                invalid field Violation.ControlCharacters
            elif value.EnumerateRunes() |> Seq.exists (fun rune -> formatting rune.Value) then
                invalid field Violation.FormattingCharacter
            else
                Ok()

    let assertedText previous (facts: Facts) =
        let changed field before after =
            if before = Some after then Ok() else admission field after

        result {
            do! changed TextInput.ClaimantName (previous |> Option.map _.Claimant) facts.Claimant

            do! changed TextInput.InsurerName (previous |> Option.map _.Insurer) facts.Insurer
        }

    let date input (value: string) =
        let field = ScalarInputs.dateTarget input
        let constraints = FieldDefinitions.calendarDate

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
        | _ -> invalid field Violation.CalendarDateRequired

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

    let amount input rawAmount rawCurrency =
        let amountField, currencyField = ScalarInputs.amountTargets input
        let amountRule = FieldDefinitions.amount
        let currencyRule = FieldDefinitions.currency

        result {
            let! valueText = representable amountField false rawAmount
            let! currency = representable currencyField false rawCurrency

            if not (matches amountRule.Grammar valueText) then
                return!
                    invalid
                        amountField
                        (Violation.DecimalFormat(
                            amountRule.MaximumIntegerDigits,
                            amountRule.MaximumFractionalDigits
                        ))
            elif not (matches currencyRule.Grammar currency) then
                return! invalid currencyField Violation.CurrencyFormat
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
        }

    let registration (input: Registration) =
        result {
            let! incident = date DateInput.IncidentDate input.IncidentDate

            let! notification =
                date DateInput.IncidentNotificationDate input.IncidentNotificationDate

            do!
                onOrBefore
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    incident
                    notification

            let! country = text TextInput.IncidentCountry input.IncidentCountry

            let! claimant = text TextInput.ClaimantName input.ClaimantName

            let! insurer = text TextInput.InsurerName input.InsurerName

            let! claimed =
                amount AmountInput.ClaimedAmount input.ClaimedAmount input.ClaimedCurrency

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
            let! decisionDate = date DateInput.PaymentDecisionDate input.PaymentDecisionDate

            let! payable =
                amount AmountInput.PayableAmount input.PayableAmount input.PayableCurrency

            return
                {
                    Date = decisionDate
                    Payable = payable
                }
        }

    let amountText (value: Amount) =
        value.Value.ToString("0.####", CultureInfo.InvariantCulture)

    let dateText (value: DateOnly) =
        value.ToString(Formats.CalendarDateFormat, CultureInfo.InvariantCulture)
