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
            IncidentNotificationDate: DateOnly
            IncidentCountry: string
            ClaimantName: string
            InsurerName: string
            ClaimedAmount: Amount
        }

    type Decision =
        {
            PaymentDecisionDate: DateOnly
            PayableAmount: Amount
        }

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
            invalid field (Violation.TooLong Admission.MaximumCharacters)
        else
            Ok value

    let private textRule =
        function
        | InputTarget.Field field -> FieldTable.textRule field
        | InputTarget.Reason -> FieldRules.freeText
        | target -> invalidArg (nameof target) "Expected a text field or reason."

    /// Apply the row's permanent grammar and its own diagnostic.
    let textField (row: FieldRow) (value: string) =
        let target = InputTarget.Field row.Field

        let constraints =
            match row.Scalar with
            | ScalarRule.Text rule -> rule
            | _ -> invalidArg (nameof row) "Expected a text field."

        representable target constraints.MaximumCharacters.IsSome value
        |> Result.bind (fun value ->
            if
                constraints.Format.Grammar
                |> Option.exists (fun pattern -> not (matches pattern value))
            then
                invalid
                    target
                    (row.GrammarViolation
                     |> Option.defaultWith (fun () ->
                         invalidOp "A grammar requires its own violation."))
            else
                Ok value)

    let text target value =
        match target with
        | InputTarget.Field field -> textField (FieldTable.forField field) value
        | InputTarget.Reason -> representable target true value
        | _ -> invalidArg (nameof target) "Expected a text field or reason."

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
        match (textRule field).MaximumCharacters with
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
            do!
                changed
                    (InputTarget.Field BusinessField.ClaimantName)
                    (previous |> Option.map _.ClaimantName)
                    facts.ClaimantName

            do!
                changed
                    (InputTarget.Field BusinessField.InsurerName)
                    (previous |> Option.map _.InsurerName)
                    facts.InsurerName
        }

    let date input (value: string) =
        let field = InputTarget.Field input
        let constraints = FieldTable.dateRule input

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

    let private amountValue (value: string) =
        if String.IsNullOrEmpty value || not (matches FieldRules.amount.Grammar value) then
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

    let canonicalAmount value =
        amountValue value
        |> Option.map (fun amount -> amount.ToString("0.####", CultureInfo.InvariantCulture))
        |> Option.defaultValue value

    let amount input currencyInput rawAmount rawCurrency =
        let amountField, currencyField =
            InputTarget.Field input, InputTarget.Field currencyInput

        let amountRule = FieldTable.amountRule input
        let currencyRule = FieldTable.currencyRule currencyInput

        result {
            let! valueText = representable amountField false rawAmount
            let! currency = representable currencyField false rawCurrency

            match amountValue valueText with
            | None ->
                return!
                    invalid
                        amountField
                        (Violation.DecimalFormat(
                            amountRule.MaximumIntegerDigits,
                            amountRule.MaximumFractionalDigits
                        ))
            | Some _ when not (matches currencyRule.Grammar currency) ->
                return! invalid currencyField Violation.CurrencyFormat
            | Some value -> return { Value = value; Currency = currency }
        }

    let registration (input: Registration) =
        result {
            let! incident = date BusinessField.IncidentDate input.IncidentDate

            let! notification =
                date BusinessField.IncidentNotificationDate input.IncidentNotificationDate

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
                    BusinessField.ClaimedAmount
                    BusinessField.ClaimedCurrency
                    input.ClaimedAmount
                    input.ClaimedCurrency

            return
                {
                    IncidentDate = incident
                    IncidentNotificationDate = notification
                    IncidentCountry = country
                    ClaimantName = claimant
                    InsurerName = insurer
                    ClaimedAmount = claimed
                }
        }

    let decision (input: ClaimCore.Contract.Decision) =
        result {
            let! decisionDate = date BusinessField.PaymentDecisionDate input.PaymentDecisionDate

            let! payable =
                amount
                    BusinessField.PayableAmount
                    BusinessField.PayableCurrency
                    input.PayableAmount
                    input.PayableCurrency

            return
                {
                    PaymentDecisionDate = decisionDate
                    PayableAmount = payable
                }
        }

    let amountText (value: Amount) =
        value.Value.ToString("0.####", CultureInfo.InvariantCulture)

    let dateText (value: DateOnly) =
        value.ToString(Formats.CalendarDateFormat, CultureInfo.InvariantCulture)
