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
                        InputTarget.PaymentDecisionDate
                        facts.NotificationDate
                        decision.Date

                match PaymentProgress.paymentDate value with
                | None -> return ()
                | Some paid ->
                    do! Validation.onOrBefore InputTarget.PaymentDate decision.Date paid

                    if decision.Payable.Value = 0M then
                        return! Error DomainError.ZeroDecisionCannotBePaid
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
                    InputTarget.IncidentDate
                    (Some before.IncidentDate)
                    (Some after.IncidentDate)

            do!
                asserted
                    InputTarget.IncidentNotificationDate
                    (Some before.NotificationDate)
                    (Some after.NotificationDate)

            do!
                asserted
                    InputTarget.PaymentDecisionDate
                    (PaymentProgress.decision prior |> Option.map _.Date)
                    (PaymentProgress.decision next |> Option.map _.Date)

            do!
                asserted
                    InputTarget.PaymentDate
                    (PaymentProgress.paymentDate prior)
                    (PaymentProgress.paymentDate next)
        }
