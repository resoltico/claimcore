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

    let admit today (before: Claim option) (after: Claim) =
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
                progress after.Facts after.Progress
                assertedDate
                    BusinessField.IncidentDate
                    (before |> Option.map _.Facts.IncidentDate)
                    (Some after.Facts.IncidentDate)
                assertedDate
                    BusinessField.IncidentNotificationDate
                    (before |> Option.map _.Facts.IncidentNotificationDate)
                    (Some after.Facts.IncidentNotificationDate)
                assertedDate
                    BusinessField.PaymentDecisionDate
                    (before
                     |> Option.bind (fun claim -> PaymentProgress.decision claim.Progress)
                     |> Option.map _.PaymentDecisionDate)
                    (PaymentProgress.decision after.Progress |> Option.map _.PaymentDecisionDate)
                assertedDate
                    BusinessField.PaymentDate
                    (before |> Option.bind (fun claim -> PaymentProgress.paymentDate claim.Progress))
                    (PaymentProgress.paymentDate after.Progress)
                assertedName
                    BusinessField.ClaimantName
                    (before |> Option.map _.Facts.ClaimantName)
                    after.Facts.ClaimantName
                assertedName
                    BusinessField.InsurerName
                    (before |> Option.map _.Facts.InsurerName)
                    after.Facts.InsurerName
            ]
