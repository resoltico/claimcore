namespace ClaimCore.Domain

/// Accepted structural invariants and current admission have distinct inputs and lifetimes.
module internal StateValidation =
    let relation asserted earlier earlierDate later laterDate =
        if earlierDate <= laterDate then
            Ok()
        elif Set.contains earlier asserted && not (Set.contains later asserted) then
            InputValidation.chronology earlier later laterDate
        else
            InputValidation.chronology later earlier earlierDate

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
    let fieldAdmission today (before: Claim option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues
                    claim.Facts
                    (PaymentProgress.decision claim.Progress)
                    (PaymentProgress.payment claim.Progress)
                |> Map.ofList)
            |> Option.defaultValue Map.empty

        fun field value ->
            if Map.tryFind field previous = Some value then
                Ok value
            else
                FieldValue.admit today value

    let assertionsAgainst today before members =
        let admit = fieldAdmission today before

        members
        |> List.map (fun (field, value) ->
            admit field value
            |> Result.map ignore
            |> Result.mapError (fun issue -> Refusal.invalidInput (InputTarget.Field field, issue)))
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

    /// Chronology uses parsed participants even when another field prevents group construction.
    let authoredChronology (before: Claim option) (act: AuthoredCaseAct) =
        let registrationUpdate (update: RegistrationUpdateInput) =
            [
                BusinessField.IncidentDate, update.IncidentDate
                BusinessField.IncidentNotificationDate, update.NotificationDate
            ]
            |> List.choose (fun (field, raw) ->
                raw |> Option.map (fun raw -> field, CalendarDate.parse raw))

        let decisionUpdate (update: DecisionUpdateInput) =
            update.Date
            |> Option.map (fun raw -> BusinessField.PaymentDecisionDate, CalendarDate.parse raw)
            |> Option.toList

        let dates parsed =
            parsed
            |> List.filter (fun (field, _) ->
                (BusinessField.describe field).Kind = FieldValueKind.CalendarDate)
            |> List.map (fun (field, value) -> field, value |> Result.map FieldValue.date)

        let correctionDates (correction: AuthoredCorrection) =
            [
                match correction.Registration with
                | Amendment.Update update -> yield! registrationUpdate update
                | Amendment.Keep -> ()
                match correction.Decision with
                | Change.Update update -> yield! decisionUpdate update
                | Change.Keep
                | Change.Clear -> ()
                match correction.Payment with
                | PaymentChange.Replace input -> yield! PaymentInput.parsed input |> dates
                | PaymentChange.Keep
                | PaymentChange.Clear
                | PaymentChange.Reaffirm -> ()
            ]

        let assertions =
            match act with
            | CaseAct.Open input -> RegistrationInput.parsed input |> dates
            | CaseAct.AmendRegistration update -> registrationUpdate update
            | CaseAct.Decide input -> DecisionInput.parsed input |> dates
            | CaseAct.RecordPayment input -> PaymentInput.parsed input |> dates
            | CaseAct.Correct correction -> correctionDates correction
            | CaseAct.Bare _
            | CaseAct.Void _
            | CaseAct.Reinstate _ -> []

        let current =
            match act, before with
            | CaseAct.Open _, _
            | _, None -> Map.empty
            | _, Some claim ->
                CaseFields.groupValues
                    claim.Facts
                    (PaymentProgress.decision claim.Progress)
                    (PaymentProgress.payment claim.Progress)
                |> List.filter (fun (_, value) ->
                    FieldValue.kind value = FieldValueKind.CalendarDate)
                |> List.map (fun (field, value) -> field, Some(FieldValue.date value))
                |> Map.ofList

        let current =
            match act with
            | CaseAct.Correct correction ->
                let current =
                    match correction.Decision with
                    | Change.Clear -> Map.remove BusinessField.PaymentDecisionDate current
                    | _ -> current

                match correction.Payment with
                | PaymentChange.Clear -> Map.remove BusinessField.PaymentDate current
                | _ -> current
            | _ -> current

        let effective =
            assertions
            |> List.fold
                (fun state (field, value) -> Map.add field (Result.toOption value) state)
                current

        let asserted = assertions |> List.map fst |> Set.ofList

        let check earlier later =
            match
                Map.tryFind earlier effective |> Option.flatten,
                Map.tryFind later effective |> Option.flatten
            with
            | Some first, Some last -> relation asserted earlier first later last
            | _ -> Ok()

        Admission.collect
            [
                check BusinessField.IncidentDate BusinessField.IncidentNotificationDate
                check BusinessField.IncidentNotificationDate BusinessField.PaymentDecisionDate
                check BusinessField.PaymentDecisionDate BusinessField.PaymentDate
            ]
