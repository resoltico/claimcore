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
                    Error Refusal.ZeroDecisionCannotBePaid
                else
                    Ok())

    let newlyAssertedDates today (before: Registration) prior (after: Registration) next =
        let asserted field previous candidate =
            match candidate with
            | None -> Ok()
            | Some date when previous = Some date -> Ok()
            | Some date -> Validation.notFuture field today date

        Admission.collect
            [
                asserted
                    (InputTarget.Field BusinessField.IncidentDate)
                    (Some before.IncidentDate)
                    (Some after.IncidentDate)
                asserted
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    (Some before.IncidentNotificationDate)
                    (Some after.IncidentNotificationDate)
                asserted
                    (InputTarget.Field BusinessField.PaymentDecisionDate)
                    (PaymentProgress.decision prior |> Option.map _.PaymentDecisionDate)
                    (PaymentProgress.decision next |> Option.map _.PaymentDecisionDate)
                asserted
                    (InputTarget.Field BusinessField.PaymentDate)
                    (PaymentProgress.paymentDate prior)
                    (PaymentProgress.paymentDate next)
            ]
