namespace ClaimCore.Domain


open ClaimCore.Domain.ResultFlow

/// Invariants apply equally to historical restoration and every newly proposed business state.
module internal StateValidation =
    let progress (facts: Registration) value =
        match PaymentProgress.decision value with
        | None -> Ok()
        | Some decision ->
            Admission.collect
                [
                    Validation.onOrBefore
                        (InputTarget.Field BusinessField.PaymentDecisionDate)
                        (Registration.incidentNotificationDate (facts))
                        (Decision.date (decision))
                    match PaymentProgress.paymentDate value with
                    | None -> Ok()
                    | Some paid ->
                        Validation.onOrBefore
                            (InputTarget.Field BusinessField.PaymentDate)
                            (Decision.date (decision))
                            paid
                ]
            |> Result.bind (fun () ->
                if
                    (PaymentProgress.paymentDate value).IsSome
                    && Money.value (Decision.payable (decision)) = 0M
                then
                    Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid)
                else
                    Ok())

    let admitGroups
        today
        (before: Claim option)
        (facts: Registration, decision: Decision option, payment)
        =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues
                    claim.Facts
                    (PaymentProgress.decision claim.Progress)
                    (PaymentProgress.paymentDate claim.Progress)
                |> Map.ofList)
            |> Option.defaultValue Map.empty

        Admission.collect
            [
                match PaymentProgress.fromGroups decision payment with
                | Ok proposed -> progress facts proposed
                | Error() -> Ok()
                for field, value in CaseFields.groupValues facts decision payment do
                    if Map.tryFind field previous = Some value then
                        Ok()
                    else
                        FieldValue.admit today value
                        |> Result.map ignore
                        |> Result.mapError (fun violation ->
                            Refusal.invalidInput (InputTarget.Field field, violation))
            ]

    let admit today before (after: Claim) =
        admitGroups
            today
            before
            (after.Facts,
             PaymentProgress.decision after.Progress,
             PaymentProgress.paymentDate after.Progress)
