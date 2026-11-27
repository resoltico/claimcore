namespace ClaimCore.Domain.Changes

open ClaimCore.Domain

module CaseDescription =
    let private isMoney =
        function
        | BusinessField.ClaimedAmount
        | BusinessField.ClaimedCurrency
        | BusinessField.PayableAmount
        | BusinessField.PayableCurrency -> true
        | _ -> false

    let private differences before after =
        BusinessField.declarations
        |> List.choose (fun declaration ->
            let field = declaration.Field

            if isMoney field then
                None
            else
                Difference.value (before field) (after field)
                |> Option.map (fun difference ->
                    {
                        CaseFieldDifference.FieldName = field
                        Before = difference.Before
                        After = difference.After
                    }))

    let private moneyDifferences claimed payable =
        [ BusinessField.ClaimedAmount, claimed; BusinessField.PayableAmount, payable ]
        |> List.choose (fun (field, (before, after)) ->
            Difference.value before after
            |> Option.map (fun change ->
                {
                    CaseMoneyDifference.FieldName = field
                    Change = change
                }))

    let opening registration =
        let values = Registration.values registration |> Map.ofList

        let value =
            function
            | BusinessField.CaseReference -> None
            | BusinessField.Status -> Some(CaseFieldValue.Status CaseStatus.Opened)
            | field -> Map.tryFind field values |> Option.map CaseFieldValue.Business

        {
            OpeningCaseDescription.Lifecycle = Lifecycle.Active
            Fields = differences (fun _ -> None) value
            Money = moneyDifferences (None, Registration.claimed registration) (None, None)
        }

    let private fields reference (value: CaseValue) =
        value.Content
        |> Option.map (fun content ->
            {
                CaseFields.CaseReference = reference
                Handling = value.Handling
                Registration = content.Registration
                Finance = content.Finance
            })

    let describe reference (before: CaseValue option) (after: CaseValue) =
        let nextReference = defaultArg after.Reference reference
        let prior = before |> Option.bind (fields reference)
        let next = fields nextReference after

        let value isAfter field =
            if field = BusinessField.CaseReference then
                if isAfter then
                    Some(CaseFieldValue.Reference nextReference)
                elif before.IsSome then
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
            differences (value false) (value true)
            @ (after.ReopenReason
               |> Option.map (fun reason ->
                   {
                       FieldName = BusinessField.ReopenReason
                       Before = None
                       After = Some(CaseFieldValue.Business(Admitted.Reason reason))
                   })
               |> Option.toList)

        let money select =
            prior |> Option.bind select, next |> Option.bind select

        let amounts =
            moneyDifferences
                (money (fun fields -> Registration.claimed fields.Registration))
                (money (fun fields -> fields.Finance.Decision |> Option.map Decision.payable))

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
