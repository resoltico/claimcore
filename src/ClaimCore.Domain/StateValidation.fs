namespace ClaimCore.Domain


open ClaimCore.Domain.ResultFlow

/// Invariants apply equally to historical restoration and every newly proposed business state.
module internal StateValidation =
    let progress (facts: Validation.Facts) value =
        result {
            match PaymentProgress.decision value with
            | None -> return ()
            | Some decision ->
                do!
                    Validation.onOrBefore
                        (InputTarget.Field BusinessField.PaymentDecisionDate)
                        facts.IncidentNotificationDate
                        decision.PaymentDecisionDate

                match PaymentProgress.paymentDate value with
                | None -> return ()
                | Some paid ->
                    do!
                        Validation.onOrBefore
                            (InputTarget.Field BusinessField.PaymentDate)
                            decision.PaymentDecisionDate
                            paid

                    if decision.PayableAmount.Value = 0M then
                        return! Error Refusal.ZeroDecisionCannotBePaid
        }

    let newlyAssertedDates today (before: Validation.Facts) prior (after: Validation.Facts) next =
        let asserted field previous candidate =
            match candidate with
            | None -> Ok()
            | Some date when previous = Some date -> Ok()
            | Some date -> Validation.notFuture field today date

        result {
            do!
                asserted
                    (InputTarget.Field BusinessField.IncidentDate)
                    (Some before.IncidentDate)
                    (Some after.IncidentDate)

            do!
                asserted
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    (Some before.IncidentNotificationDate)
                    (Some after.IncidentNotificationDate)

            do!
                asserted
                    (InputTarget.Field BusinessField.PaymentDecisionDate)
                    (PaymentProgress.decision prior |> Option.map _.PaymentDecisionDate)
                    (PaymentProgress.decision next |> Option.map _.PaymentDecisionDate)

            do!
                asserted
                    (InputTarget.Field BusinessField.PaymentDate)
                    (PaymentProgress.paymentDate prior)
                    (PaymentProgress.paymentDate next)
        }
