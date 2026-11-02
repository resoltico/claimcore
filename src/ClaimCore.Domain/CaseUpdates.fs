namespace ClaimCore.Domain

/// Complete money assertion: an update cannot carry an amount without its currency.
type MoneyInput = { Amount: string; Currency: string }

type RegistrationUpdate<'date, 'country, 'name, 'money> =
    {
        IncidentDate: 'date option
        NotificationDate: 'date option
        Country: 'country option
        Claimant: 'name option
        Insurer: 'name option
        Claimed: 'money option
    }

type RegistrationUpdateInput = RegistrationUpdate<string, string, string, MoneyInput>
type ValidRegistrationUpdate = RegistrationUpdate<CalendarDate, CountryCode, PartyName, Money>

type DecisionUpdate<'date, 'money> =
    {
        Date: 'date option
        Payable: 'money option
    }

type DecisionUpdateInput = DecisionUpdate<string, MoneyInput>
type ValidDecisionUpdate = DecisionUpdate<CalendarDate, Money>

module private UpdateParsing =
    let optional parse =
        function
        | None -> Ok None
        | Some value -> parse value |> Result.map Some

    let errors field =
        function
        | Ok _ -> []
        | Error issue -> [ field, issue ]

    let value result =
        result
        |> Result.defaultWith (fun _ -> invalidOp "Update parsing must succeed first.")

module MoneyInput =
    let parse amountField currencyField (input: MoneyInput) =
        let amount = Amount.parse input.Amount
        let currency = Currency.parse input.Currency

        let issues =
            UpdateParsing.errors amountField amount
            @ UpdateParsing.errors currencyField currency

        if List.isEmpty issues then
            Ok(Money.create (UpdateParsing.value amount) (UpdateParsing.value currency))
        else
            Error issues

    let ofMoney money =
        {
            Amount = Amount.format (Money.amount money)
            Currency = Currency.format (Money.currencyValue money)
        }

module RegistrationUpdate =
    let map date country name money (input: RegistrationUpdate<_, _, _, _>) =
        {
            IncidentDate = Option.map date input.IncidentDate
            NotificationDate = Option.map date input.NotificationDate
            Country = Option.map country input.Country
            Claimant = Option.map name input.Claimant
            Insurer = Option.map name input.Insurer
            Claimed = Option.map money input.Claimed
        }

    let values (update: ValidRegistrationUpdate) =
        [
            yield!
                update.IncidentDate
                |> Option.map (fun v -> BusinessField.IncidentDate, Admitted.Date v)
                |> Option.toList
            yield!
                update.NotificationDate
                |> Option.map (fun v -> BusinessField.IncidentNotificationDate, Admitted.Date v)
                |> Option.toList
            yield!
                update.Country
                |> Option.map (fun v -> BusinessField.IncidentCountry, Admitted.Country v)
                |> Option.toList
            yield!
                update.Claimant
                |> Option.map (fun v -> BusinessField.ClaimantName, Admitted.Name v)
                |> Option.toList
            yield!
                update.Insurer
                |> Option.map (fun v -> BusinessField.InsurerName, Admitted.Name v)
                |> Option.toList
            match update.Claimed with
            | None -> ()
            | Some money ->
                yield BusinessField.ClaimedAmount, Admitted.Amount(Money.amount money)
                yield BusinessField.ClaimedCurrency, Admitted.Currency(Money.currencyValue money)
        ]

    let authored update : RegistrationUpdateInput =
        map CalendarDate.format CountryCode.format PartyName.format MoneyInput.ofMoney update

    /// Merge validated structure directly. Untouched values keep their original accepted meaning.
    let apply (update: ValidRegistrationUpdate) current =
        let incident = defaultArg update.IncidentDate (Registration.incidentDate current)

        let notification =
            defaultArg update.NotificationDate (Registration.incidentNotificationDate current)

        if incident > notification then
            let target =
                if update.IncidentDate.IsSome && update.NotificationDate.IsNone then
                    BusinessField.IncidentDate
                else
                    BusinessField.IncidentNotificationDate

            let counterpart, date =
                if target = BusinessField.IncidentDate then
                    BusinessField.IncidentNotificationDate, notification
                else
                    BusinessField.IncidentDate, incident

            Error(target, counterpart, date)
        else
            Registration.replace (values update) current
            |> Result.mapError (fun _ ->
                invalidOp "A typed registration merge with valid chronology must succeed.")

module RegistrationUpdateInput =
    let parse (input: RegistrationUpdateInput) =
        let incident = UpdateParsing.optional CalendarDate.parse input.IncidentDate
        let notification = UpdateParsing.optional CalendarDate.parse input.NotificationDate
        let country = UpdateParsing.optional CountryCode.parse input.Country
        let claimant = UpdateParsing.optional PartyName.parse input.Claimant
        let insurer = UpdateParsing.optional PartyName.parse input.Insurer

        let money =
            UpdateParsing.optional
                (MoneyInput.parse BusinessField.ClaimedAmount BusinessField.ClaimedCurrency)
                input.Claimed

        let issues =
            UpdateParsing.errors BusinessField.IncidentDate incident
            @ UpdateParsing.errors BusinessField.IncidentNotificationDate notification
            @ UpdateParsing.errors BusinessField.IncidentCountry country
            @ UpdateParsing.errors BusinessField.ClaimantName claimant
            @ UpdateParsing.errors BusinessField.InsurerName insurer
            @ (match money with
               | Ok _ -> []
               | Error errors -> errors)

        if not (List.isEmpty issues) then
            Error issues
        elif
            input.IncidentDate.IsNone
            && input.NotificationDate.IsNone
            && input.Country.IsNone
            && input.Claimant.IsNone
            && input.Insurer.IsNone
            && input.Claimed.IsNone
        then
            Error [ BusinessField.IncidentDate, Violation.UpdateRequired ]
        else
            Ok
                {
                    ValidRegistrationUpdate.IncidentDate = UpdateParsing.value incident
                    NotificationDate = UpdateParsing.value notification
                    Country = UpdateParsing.value country
                    Claimant = UpdateParsing.value claimant
                    Insurer = UpdateParsing.value insurer
                    Claimed = UpdateParsing.value money
                }

module DecisionUpdate =
    let map date money (input: DecisionUpdate<_, _>) =
        {
            Date = Option.map date input.Date
            Payable = Option.map money input.Payable
        }

    let values (update: ValidDecisionUpdate) =
        [
            yield!
                update.Date
                |> Option.map (fun v -> BusinessField.PaymentDecisionDate, Admitted.Date v)
                |> Option.toList
            match update.Payable with
            | None -> ()
            | Some money ->
                yield BusinessField.PayableAmount, Admitted.Amount(Money.amount money)
                yield BusinessField.PayableCurrency, Admitted.Currency(Money.currencyValue money)
        ]

    let authored update : DecisionUpdateInput =
        map CalendarDate.format MoneyInput.ofMoney update

    let apply update current =
        Decision.replace (values update) current
        |> Result.defaultWith (fun _ -> invalidOp "A typed decision merge must succeed.")

module DecisionUpdateInput =
    let parse (input: DecisionUpdateInput) =
        let date = UpdateParsing.optional CalendarDate.parse input.Date

        let payable =
            UpdateParsing.optional
                (MoneyInput.parse BusinessField.PayableAmount BusinessField.PayableCurrency)
                input.Payable

        let issues =
            UpdateParsing.errors BusinessField.PaymentDecisionDate date
            @ (match payable with
               | Ok _ -> []
               | Error issues -> issues)

        if not (List.isEmpty issues) then
            Error issues
        elif input.Date.IsNone && input.Payable.IsNone then
            Error [ BusinessField.PaymentDecisionDate, Violation.UpdateRequired ]
        else
            Ok
                {
                    ValidDecisionUpdate.Date = UpdateParsing.value date
                    Payable = UpdateParsing.value payable
                }
