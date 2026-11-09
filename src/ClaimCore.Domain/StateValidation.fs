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

    let crossProgress
        asserted
        notification
        (decision: Decision option)
        (payments: PaymentEntry list)
        =
        let collection =
            if
                (payments |> List.map PaymentEntry.id |> Set.ofList |> Set.count)
                <> payments.Length
            then
                Error(Refusal.IdempotencyConflict)
            else
                Ok()

        let decisionRules =
            match decision with
            | None when not payments.IsEmpty -> Error(Refusal.Case CaseRefusal.DecisionHasPayments)
            | None -> Ok()
            | Some decision ->
                let total = payments |> List.sumBy (PaymentEntry.amount >> Amount.value)

                Admission.collect
                    [
                        relation
                            asserted
                            BusinessField.IncidentNotificationDate
                            notification
                            BusinessField.PaymentDecisionDate
                            (Decision.date decision)
                        match
                            payments |> List.map PaymentEntry.date |> List.sort |> List.tryHead
                        with
                        | None -> Ok()
                        | Some first ->
                            relation
                                asserted
                                BusinessField.PaymentDecisionDate
                                (Decision.date decision)
                                BusinessField.PaymentDate
                                first
                        if total <= Money.value (Decision.payable decision) then
                            Ok()
                        else
                            let target =
                                if Set.contains BusinessField.PaidAmount asserted then
                                    BusinessField.PaidAmount
                                else
                                    BusinessField.PayableAmount

                            InputValidation.invalid
                                (InputTarget.Field target)
                                Violation.PaymentExceedsPayable
                    ]

        Admission.collect [ collection; decisionRules ]

    let decisionChange before after =
        match PaymentProgress.decision before, after with
        | Some previous, Some next when
            not (PaymentProgress.payments before).IsEmpty
            && Money.currency (Decision.payable previous)
               <> Money.currency (Decision.payable next)
            ->
            InputValidation.invalid
                (InputTarget.Field BusinessField.PayableCurrency)
                Violation.PaymentCurrencyMismatch
        | _ -> Ok()

    let progress (facts: Registration) value =
        crossProgress
            Set.empty
            (Registration.incidentNotificationDate facts)
            (PaymentProgress.decision value)
            (PaymentProgress.payments value)
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                let issue = InputIssues.head issues
                RecordViolation.InvalidScalar(issue.Target, issue.Violation)
            | _ -> RecordViolation.InvalidProgress)
        |> Result.bind (fun () ->
            match
                PaymentProgress.fromGroups
                    (PaymentProgress.decision value)
                    (PaymentProgress.payments value)
            with
            | Ok expected when expected = value -> Ok()
            | _ -> Error RecordViolation.InvalidProgress)

    /// Equality with accepted state preserves existing reassertion semantics; omission never re-admits.
    let fieldAdmission today (before: Claim option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues claim.Facts (PaymentProgress.decision claim.Progress)
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

    let admit today before (after: Claim) =
        Admission.collect
            [
                crossProgress
                    Set.empty
                    (Registration.incidentNotificationDate after.Facts)
                    (PaymentProgress.decision after.Progress)
                    (PaymentProgress.payments after.Progress)
                assertionsAgainst
                    today
                    before
                    (CaseFields.groupValues after.Facts (PaymentProgress.decision after.Progress))
            ]

    /// Parsed participants retain sibling chronology even when another scalar prevents a complete command.
    let preparedChronology (before: Claim option) opening clearing assertions =
        let current =
            match opening, before with
            | true, _
            | _, None -> Map.empty
            | false, Some claim ->
                (CaseFields.groupValues claim.Facts (PaymentProgress.decision claim.Progress)
                 @ (PaymentProgress.payments claim.Progress
                    |> List.map PaymentEntry.date
                    |> List.sort
                    |> List.tryHead
                    |> Option.map (fun date -> [ BusinessField.PaymentDate, Admitted.Date date ])
                    |> Option.defaultValue []))
                |> List.filter (fun (_, value) ->
                    FieldValue.kind value = FieldValueKind.CalendarDate)
                |> List.map (fun (field, value) -> field, Some(FieldValue.date value))
                |> Map.ofList

        let current =
            if clearing then
                Map.remove BusinessField.PaymentDecisionDate current
            else
                current

        let effective =
            assertions
            |> List.fold (fun state (field, value) -> Map.add field value state) current

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
