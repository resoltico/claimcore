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
                        facts.IncidentNotificationDate
                        decision.PaymentDecisionDate
                    match PaymentProgress.paymentDate value with
                    | None -> Ok()
                    | Some paid ->
                        Validation.onOrBefore
                            (InputTarget.Field BusinessField.PaymentDate)
                            decision.PaymentDecisionDate
                            paid
                ]
            |> Result.bind (fun () ->
                if
                    (PaymentProgress.paymentDate value).IsSome
                    && Money.value decision.PayableAmount = 0M
                then
                    Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid)
                else
                    Ok())

    let admitGroups
        today
        (before: Claim option)
        (facts: Registration, decision: Decision option, payment)
        =
        let assertedDate field previous candidate =
            match candidate with
            | None -> Ok()
            | Some date when previous = Some date -> Ok()
            | Some date -> Validation.notFuture (InputTarget.Field field) today date

        let assertedName field previous candidate =
            if previous = Some candidate then
                Ok()
            else
                PartyName.admit candidate
                |> Result.map ignore
                |> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.Field field, violation))

        Admission.collect
            [
                // An incomplete correction is refused by its acknowledgement/change checks.
                // Its independently asserted values must still contribute input issues.
                match PaymentProgress.fromGroups decision payment with
                | Ok proposed -> progress facts proposed
                | Error() -> Ok()
                assertedDate
                    BusinessField.IncidentDate
                    (before |> Option.map _.Facts.IncidentDate)
                    (Some facts.IncidentDate)
                assertedDate
                    BusinessField.IncidentNotificationDate
                    (before |> Option.map _.Facts.IncidentNotificationDate)
                    (Some facts.IncidentNotificationDate)
                assertedDate
                    BusinessField.PaymentDecisionDate
                    (before
                     |> Option.bind (fun claim -> PaymentProgress.decision claim.Progress)
                     |> Option.map _.PaymentDecisionDate)
                    (decision |> Option.map _.PaymentDecisionDate)
                assertedDate
                    BusinessField.PaymentDate
                    (before |> Option.bind (fun claim -> PaymentProgress.paymentDate claim.Progress))
                    payment
                assertedName
                    BusinessField.ClaimantName
                    (before |> Option.map _.Facts.ClaimantName)
                    facts.ClaimantName
                assertedName
                    BusinessField.InsurerName
                    (before |> Option.map _.Facts.InsurerName)
                    facts.InsurerName
            ]

    let admit today before (after: Claim) =
        admitGroups
            today
            before
            (after.Facts,
             PaymentProgress.decision after.Progress,
             PaymentProgress.paymentDate after.Progress)
