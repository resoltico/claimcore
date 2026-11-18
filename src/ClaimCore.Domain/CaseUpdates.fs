namespace ClaimCore.Domain

/// Complete money assertion: an update cannot carry an amount without its currency.
type MoneyInput = { Amount: string; Currency: string }

/// Omitted keys preserve a fact; a present None explicitly clears an optional fact.
type RegistrationUpdate<'value> = private RegistrationUpdate of Map<BusinessField, 'value option>
type RegistrationUpdateInput = RegistrationUpdate<string>
type ValidRegistrationUpdate = RegistrationUpdate<Admitted>

type DecisionUpdate<'date, 'money, 'note> =
    {
        Date: 'date option
        Payable: 'money option
        Basis: 'note option option
    }

type DecisionUpdateInput = DecisionUpdate<string, MoneyInput, string>
type ValidDecisionUpdate = DecisionUpdate<CalendarDate, Money, NoteText>

module private UpdateParsing =
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
                    let assignments = Map.ofList members

                    Option.map Option.isSome (Map.tryFind field assignments)
                    <> Option.map Option.isSome (Map.tryFind currency assignments)))
        then
            Error "A money update requires both amount and currency."
        else
            Ok(Map.ofList members)

    let prepare fields =
        fields
        |> Map.toList
        |> List.map (fun (field, raw) ->
            field,
            match raw with
            | None -> Ok None
            | Some value ->
                FieldValue.parse (BusinessField.describe field).Kind value |> Result.map Some)

    let admitPrepared emptyTarget admit prepared =
        let parsed =
            prepared
            |> List.map (fun (field, result) ->
                field,
                result
                |> Result.bind (function
                    | None -> Ok None
                    | Some value -> admit field value |> Result.map Some))

        let issues =
            parsed
            |> List.collect (fun (field, result) -> UpdateParsing.errors field result)

        if not issues.IsEmpty then
            Error issues
        elif List.isEmpty prepared then
            Error [ emptyTarget, Violation.UpdateRequired ]
        else
            parsed
            |> List.map (fun (field, result) -> field, UpdateParsing.value result)
            |> Map.ofList
            |> Ok

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
        changes update
        |> List.fold
            (fun values (field, value) ->
                match value with
                | None -> Map.remove field values
                | Some value -> Map.add field value values)
            (Registration.values current |> Map.ofList)
        |> Map.toList
        |> Registration.create
        |> Result.defaultWith invalidOp

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

    let internal prepare (RegistrationUpdate fields: RegistrationUpdateInput) =
        UpdateFields.prepare fields

    let internal parsePreparedWith admit prepared =
        UpdateFields.admitPrepared BusinessField.IncidentDate admit prepared
        |> Result.bind (fun fields ->
            match
                Map.tryFind BusinessField.ClaimedAmount fields,
                Map.tryFind BusinessField.ClaimedCurrency fields
            with
            | Some(Some _), Some None ->
                Error [ BusinessField.ClaimedCurrency, Violation.TextRequired ]
            | Some None, Some(Some _) ->
                Error [ BusinessField.ClaimedAmount, Violation.TextRequired ]
            | _ -> Ok(RegistrationUpdate fields))

    let internal parseWith admit input =
        prepare input |> parsePreparedWith admit

    let parse input = parseWith (fun _ -> Ok) input

module DecisionUpdate =
    let map date money note (input: DecisionUpdate<_, _, _>) =
        {
            Date = Option.map date input.Date
            Payable = Option.map money input.Payable
            Basis = input.Basis |> Option.map (Option.map note)
        }

    let values (update: ValidDecisionUpdate) =
        [
            yield!
                update.Date
                |> Option.map (fun v -> BusinessField.PaymentDecisionDate, Admitted.Date v)
                |> Option.toList
            match update.Basis |> Option.flatten with
            | Some note -> yield BusinessField.DecisionBasis, Admitted.Note note
            | None -> ()
            match update.Payable with
            | None -> ()
            | Some money ->
                yield BusinessField.PayableAmount, Admitted.Amount(Money.amount money)
                yield BusinessField.PayableCurrency, Admitted.Currency(Money.currencyValue money)
        ]

    let authored update : DecisionUpdateInput =
        map CalendarDate.format MoneyInput.ofMoney NoteText.format update

    let apply update current =
        let original =
            if update.Basis = Some None then
                Decision.clearBasis current
            else
                current

        Decision.replace (values update) original
        |> Result.defaultWith (fun _ -> invalidOp "A typed decision merge must succeed.")

module DecisionUpdateInput =
    let values (input: DecisionUpdateInput) =
        [
            yield!
                input.Date
                |> Option.map (fun value -> BusinessField.PaymentDecisionDate, Some value)
                |> Option.toList
            match input.Basis with
            | Some note -> yield BusinessField.DecisionBasis, note
            | None -> ()
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
                Basis = Map.tryFind BusinessField.DecisionBasis fields
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

    let internal prepare input =
        values input |> Map.ofList |> UpdateFields.prepare

    let internal parsePreparedWith admit prepared =
        UpdateFields.admitPrepared BusinessField.PaymentDecisionDate admit prepared
        |> Result.map (fun fields ->
            let value field =
                Map.tryFind field fields |> Option.flatten

            {
                Basis =
                    Map.tryFind BusinessField.DecisionBasis fields
                    |> Option.map (Option.map FieldValue.note)
                ValidDecisionUpdate.Date =
                    value BusinessField.PaymentDecisionDate |> Option.map FieldValue.date
                Payable =
                    match
                        value BusinessField.PayableAmount, value BusinessField.PayableCurrency
                    with
                    | Some amount, Some currency ->
                        Some(
                            Money.create (FieldValue.amount amount) (FieldValue.currency currency)
                        )
                    | None, None -> None
                    | _ -> invalidOp "A prepared decision update retains its complete money pair."
            })

    let internal parseWith admit input =
        prepare input |> parsePreparedWith admit

    let parse input = parseWith (fun _ -> Ok) input
