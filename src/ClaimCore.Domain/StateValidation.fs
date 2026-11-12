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

    let crossFinancial
        asserted
        notification
        (decision: Decision option)
        (position: FinancialPosition)
        =
        match decision with
        | None when position.PaymentCount > 0L ->
            Error(Refusal.Case CaseRefusal.DecisionHasPayments)
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
                    match position.PaymentDates with
                    | None -> Ok()
                    | Some dates ->
                        relation
                            asserted
                            BusinessField.PaymentDecisionDate
                            (Decision.date decision)
                            BusinessField.PaymentDate
                            dates.Earliest
                    if
                        Amount.value position.PaidAmount <= Money.value (Decision.payable decision)
                    then
                        Ok()
                    else
                        InputValidation.invalid
                            (InputTarget.Field BusinessField.PayableAmount)
                            Violation.PaymentExceedsPayable
                ]

    let decisionChange (before: FinancialPosition) after =
        match before.Decision, after with
        | Some previous, Some next when
            before.PaymentCount > 0L
            && Money.currency (Decision.payable previous)
               <> Money.currency (Decision.payable next)
            ->
            InputValidation.invalid
                (InputTarget.Field BusinessField.PayableCurrency)
                Violation.PaymentCurrencyMismatch
        | _ -> Ok()

    let financial (facts: Registration) (position: FinancialPosition) =
        crossFinancial
            Set.empty
            (Registration.incidentNotificationDate facts)
            position.Decision
            position
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                let issue = InputIssues.head issues in
                RecordViolation.InvalidScalar(issue.Target, issue.Violation)
            | _ -> RecordViolation.InvalidFinancialPosition)

    /// Equality with accepted state preserves existing reassertion semantics; omission never re-admits.
    let fieldAdmission today (before: Claim option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues claim.Facts claim.Finance.Decision |> Map.ofList)
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
                crossFinancial
                    Set.empty
                    (Registration.incidentNotificationDate after.Facts)
                    after.Finance.Decision
                    after.Finance
                assertionsAgainst
                    today
                    before
                    (CaseFields.groupValues after.Facts after.Finance.Decision)
            ]

    /// Parsed participants retain sibling chronology even when another scalar prevents a complete command.
    let preparedChronology (before: Claim option) opening clearing assertions =
        let current =
            match opening, before with
            | true, _
            | _, None -> Map.empty
            | false, Some claim ->
                (CaseFields.groupValues claim.Facts claim.Finance.Decision
                 @ (claim.Finance.PaymentDates
                    |> Option.map _.Earliest
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
