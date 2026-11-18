namespace ClaimCore.Domain.Changes

open ClaimCore.Domain

module CaseDescription =
    let private fields reference (value: CaseValue) =
        value.Content
        |> Option.map (fun content ->
            {
                CaseFields.CaseReference = reference
                Status = value.Status
                Registration = content.Registration
                Finance = content.Finance
            })

    let describe reference (before: CaseValue option) (after: CaseValue) =
        let prior = before |> Option.bind (fields reference)
        let next = fields reference after

        let moneyFields =
            [
                BusinessField.ClaimedAmount
                BusinessField.ClaimedCurrency
                BusinessField.PayableAmount
                BusinessField.PayableCurrency
            ]

        let value isAfter field =
            if field = BusinessField.CaseReference then
                if isAfter || before.IsSome then
                    Some(CaseFieldValue.Reference reference)
                else
                    None
            elif field = BusinessField.Status then
                (if isAfter then
                     Some after.Status
                 else
                     before |> Option.map _.Status)
                |> Option.map CaseFieldValue.Status
            else
                (if isAfter then next else prior) |> Option.bind (CaseFields.typedValue field)

        let changes =
            BusinessField.declarations
            |> List.choose (fun declaration ->
                if List.contains declaration.Field moneyFields then
                    None
                else
                    Difference.value (value false declaration.Field) (value true declaration.Field)
                    |> Option.map (fun difference ->
                        {
                            CaseFieldDifference.FieldName = declaration.Field
                            Before = difference.Before
                            After = difference.After
                        }))

        let money select =
            Difference.value (prior |> Option.bind select) (next |> Option.bind select)

        let amounts =
            [
                BusinessField.ClaimedAmount,
                money (fun fields -> Registration.claimed fields.Registration)
                BusinessField.PayableAmount,
                money (fun fields -> fields.Finance.Decision |> Option.map Decision.payable)
                BusinessField.PaidAmount,
                money (fun fields -> FinancialPosition.totals fields.Finance |> Option.map _.Paid)
            ]
            |> List.choose (fun (field, change) ->
                change
                |> Option.map (fun change ->
                    {
                        CaseMoneyDifference.FieldName = field
                        Change = change
                    }))

        {
            CasePartDescription.Values = { Before = prior; After = next }
            Lifecycle = Difference.value (before |> Option.map _.Lifecycle) (Some after.Lifecycle)
            Fields = changes
            Money = amounts
            Disposition = after.Disposition
        }

    let fromChanges reference (before: PartChange option) (after: PartChange) =
        let prior =
            before
            |> Option.bind (fun change ->
                match change.Value with
                | PartValue.Case value -> Some value
                | _ -> None)

        match after.Value with
        | PartValue.Case value when before.IsSome = prior.IsSome ->
            Ok(describe reference prior value)
        | _ -> Error RecordViolation.InvalidSubjectState
