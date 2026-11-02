namespace ClaimCore.Domain

/// Accepted structural invariants and current admission have distinct inputs and lifetimes.
module internal StateValidation =
    let private relation asserted earlier earlierDate later laterDate =
        if earlierDate <= laterDate then
            Ok()
        elif Set.contains earlier asserted && not (Set.contains later asserted) then
            Validation.chronology earlier later laterDate
        else
            Validation.chronology later earlier earlierDate

    let crossProgress asserted notification (decision: Decision option) (payment: Payment option) =
        match decision with
        | None -> Ok()
        | Some decision ->
            Admission.collect
                [
                    relation
                        asserted
                        BusinessField.IncidentNotificationDate
                        notification
                        BusinessField.PaymentDecisionDate
                        (Decision.date decision)
                    match payment with
                    | None -> Ok()
                    | Some paid ->
                        relation
                            asserted
                            BusinessField.PaymentDecisionDate
                            (Decision.date decision)
                            BusinessField.PaymentDate
                            (Payment.date paid)
                ]
            |> Result.bind (fun () ->
                if payment.IsSome && Money.value (Decision.payable decision) = 0M then
                    Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid)
                else
                    Ok())

    let progress (facts: Registration) value =
        crossProgress
            Set.empty
            (Registration.incidentNotificationDate facts)
            (PaymentProgress.decision value)
            (PaymentProgress.payment value)

    /// Equality with accepted state preserves existing reassertion semantics; omission never re-admits.
    let assertionsAgainst today (before: Claim option) members =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues
                    claim.Facts
                    (PaymentProgress.decision claim.Progress)
                    (PaymentProgress.payment claim.Progress)
                |> Map.ofList)
            |> Option.defaultValue Map.empty

        members
        |> List.map (fun (field, value) ->
            if Map.tryFind field previous = Some value then
                Ok()
            else
                FieldValue.admit today value
                |> Result.map ignore
                |> Result.mapError (fun issue ->
                    Refusal.invalidInput (InputTarget.Field field, issue)))
        |> Admission.collect

    let admitGroups today before (facts, decision, payment) =
        Admission.collect
            [
                crossProgress
                    Set.empty
                    (Registration.incidentNotificationDate facts)
                    decision
                    payment
                assertionsAgainst today before (CaseFields.groupValues facts decision payment)
            ]

    let admit today before (after: Claim) =
        admitGroups
            today
            before
            (after.Facts,
             PaymentProgress.decision after.Progress,
             PaymentProgress.payment after.Progress)
