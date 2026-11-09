namespace ClaimCore.Domain

/// Complete money assertion: an update cannot carry an amount without its currency.
type MoneyInput = { Amount: string; Currency: string }

/// Omitted keys preserve a fact; a present None explicitly clears an optional fact.
type RegistrationUpdate<'value> = private RegistrationUpdate of Map<BusinessField, 'value option>
type RegistrationUpdateInput = RegistrationUpdate<string>
type ValidRegistrationUpdate = RegistrationUpdate<Admitted>

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
    let internal parseWith admit amountField currencyField (input: MoneyInput) =
        let amount =
            Amount.parse input.Amount
            |> Result.bind (Admitted.Amount >> admit amountField >> Result.map FieldValue.amount)

        let currency =
            Currency.parse input.Currency
            |> Result.bind (
                Admitted.Currency >> admit currencyField >> Result.map FieldValue.currency
            )

        let issues =
            UpdateParsing.errors amountField amount
            @ UpdateParsing.errors currencyField currency

        if List.isEmpty issues then
            Ok(Money.create (UpdateParsing.value amount) (UpdateParsing.value currency))
        else
            Error issues

    let parse amountField currencyField input =
        parseWith (fun _ -> Ok) amountField currencyField input

    let ofMoney money =
        {
            Amount = Amount.format (Money.amount money)
            Currency = Currency.format (Money.currencyValue money)
        }

module private UpdateFields =
    let validate group (members: (BusinessField * 'value option) list) =
        let keys = members |> List.map fst
        let allowed = BusinessField.ofGroup group |> Set.ofList
        let supplied = Set.ofList keys

        if keys.Length <> supplied.Count || not (Set.isSubset supplied allowed) then
            Error "Undeclared or duplicate update fields."
        elif
            members
            |> List.exists (fun (field, value) ->
                value.IsNone && not (BusinessField.describe field).AllowsAbsence)
        then
            Error "Required facts cannot be cleared."
        elif
            allowed
            |> Seq.exists (fun field ->
                (BusinessField.describe field).Pairs
                |> Option.exists (fun currency ->
                    Set.contains field supplied <> Set.contains currency supplied))
        then
            Error "A money update requires both amount and currency."
        else
            Ok(Map.ofList members)

    let parseWith admit fields =
        let parsed =
            fields
            |> Map.toList
            |> List.map (fun (field, raw) ->
                field,
                match raw with
                | None -> Ok None
                | Some value ->
                    FieldValue.parse (BusinessField.describe field).Kind value
                    |> Result.bind (admit field)
                    |> Result.map Some)

        let issues =
            parsed
            |> List.collect (fun (field, result) -> UpdateParsing.errors field result)

        if not issues.IsEmpty then
            Error issues
        elif Map.isEmpty fields then
            Error [ BusinessField.IncidentDate, Violation.UpdateRequired ]
        else
            Ok(
                parsed
                |> List.map (fun (field, result) -> field, UpdateParsing.value result)
                |> Map.ofList
            )

module RegistrationUpdate =
    let changes (RegistrationUpdate fields: ValidRegistrationUpdate) = Map.toList fields

    let values update =
        changes update
        |> List.choose (fun (field, value) -> value |> Option.map (fun value -> field, value))

    let tryValue field (RegistrationUpdate fields: ValidRegistrationUpdate) =
        Map.tryFind field fields |> Option.flatten

    let authored (RegistrationUpdate fields: ValidRegistrationUpdate) : RegistrationUpdateInput =
        fields |> Map.map (fun _ -> Option.map FieldValue.format) |> RegistrationUpdate

    let apply update current =
        let date field previous =
            tryValue field update
            |> Option.map FieldValue.date
            |> Option.defaultValue previous

        let incident = date BusinessField.IncidentDate (Registration.incidentDate current)

        let notification =
            date
                BusinessField.IncidentNotificationDate
                (Registration.incidentNotificationDate current)

        if incident > notification then
            let target =
                if
                    (tryValue BusinessField.IncidentDate update).IsSome
                    && (tryValue BusinessField.IncidentNotificationDate update).IsNone
                then
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
            changes update
            |> List.fold
                (fun values (field, value) ->
                    match value with
                    | None -> Map.remove field values
                    | Some value -> Map.add field value values)
                (Registration.values current |> Map.ofList)
            |> Map.toList
            |> Registration.create
            |> Result.mapError (fun _ -> invalidOp "A validated registration merge must succeed.")

module RegistrationUpdateInput =
    let create members =
        UpdateFields.validate RecordGroup.Registration members
        |> Result.map RegistrationUpdate

    let values (RegistrationUpdate fields: RegistrationUpdateInput) = Map.toList fields

    let tryValue field (RegistrationUpdate fields: RegistrationUpdateInput) =
        Map.tryFind field fields |> Option.flatten

    let replace members input =
        members
        |> List.fold
            (fun fields (field, value) -> Map.add field value fields)
            (values input |> Map.ofList)
        |> Map.toList
        |> create

    let internal parseWith admit (RegistrationUpdate fields: RegistrationUpdateInput) =
        UpdateFields.parseWith admit fields |> Result.map RegistrationUpdate

    let parse input = parseWith (fun _ -> Ok) input

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
    let values (input: DecisionUpdateInput) =
        [
            yield!
                input.Date
                |> Option.map (fun value -> BusinessField.PaymentDecisionDate, Some value)
                |> Option.toList
            match input.Payable with
            | None -> ()
            | Some value ->
                yield BusinessField.PayableAmount, Some value.Amount
                yield BusinessField.PayableCurrency, Some value.Currency
        ]

    let create members =
        UpdateFields.validate RecordGroup.Decision members
        |> Result.map (fun fields ->
            let value field =
                Map.tryFind field fields |> Option.flatten

            {
                DecisionUpdateInput.Date = value BusinessField.PaymentDecisionDate
                Payable =
                    match
                        value BusinessField.PayableAmount, value BusinessField.PayableCurrency
                    with
                    | Some amount, Some currency ->
                        Some
                            {
                                MoneyInput.Amount = amount
                                Currency = currency
                            }
                    | _ -> None
            })

    let internal parseWith admit (input: DecisionUpdateInput) =
        let date =
            UpdateParsing.optional
                (fun raw ->
                    CalendarDate.parse raw
                    |> Result.bind (
                        Admitted.Date
                        >> admit BusinessField.PaymentDecisionDate
                        >> Result.map FieldValue.date
                    ))
                input.Date

        let payable =
            UpdateParsing.optional
                (MoneyInput.parseWith
                    admit
                    BusinessField.PayableAmount
                    BusinessField.PayableCurrency)
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

    let parse input = parseWith (fun _ -> Ok) input
