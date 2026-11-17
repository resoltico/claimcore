namespace ClaimCore.Domain.Inputs

open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal CaseInput =
    let invalid field violation =
        Error(Refusal.invalidInput (field, violation))

    let chronology target counterpart date =
        Error(
            Refusal.InvalidInput(
                InputIssues.ofList
                    [
                        {
                            InputIssue.Target = InputTarget.Field target
                            Violation = Violation.ChronologicalOrder
                            GrantRow = None
                            RelatedDate = Some { Field = counterpart; Value = date }
                        }
                    ]
                |> Option.get
            )
        )

    let registrationMerge result =
        result
        |> Result.mapError (fun (target, counterpart, date) ->
            Refusal.InvalidInput(
                InputIssues.ofList
                    [
                        {
                            InputIssue.Target = InputTarget.Field target
                            Violation = Violation.ChronologicalOrder
                            GrantRow = None
                            RelatedDate = Some { Field = counterpart; Value = date }
                        }
                    ]
                |> Option.get
            ))

    let ordered result =
        result
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.groupBy (fun issue -> issue.Target, issue.GrantRow, issue.Violation)
                |> List.collect (fun (_, same) ->
                    let related = same |> List.filter (fun issue -> issue.RelatedDate.IsSome)

                    if related.IsEmpty then
                        [ same.Head ]
                    else
                        List.distinct related)
                |> List.sortBy (fun issue -> InputTarget.order issue.Target)
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
            | refusal -> refusal)

/// Accepted structural invariants and current admission have distinct inputs and lifetimes.
module internal CaseAdmission =
    let relation asserted earlier earlierDate later laterDate =
        if earlierDate <= laterDate then
            Ok()
        elif Set.contains earlier asserted && not (Set.contains later asserted) then
            CaseInput.chronology earlier later laterDate
        else
            CaseInput.chronology later earlier earlierDate

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
                        CaseInput.invalid
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
            CaseInput.invalid
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
    let fieldAdmission today (before: CaseContent option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues claim.Registration claim.Finance.Decision |> Map.ofList)
            |> Option.defaultValue Map.empty

        fun field value ->
            if Map.tryFind field previous = Some value then
                Ok value
            else
                FieldValue.admit today value

    /// Parsed participants retain sibling chronology even when another scalar prevents a complete command.
    let preparedChronology (before: CaseContent option) opening clearing assertions =
        let current =
            match opening, before with
            | true, _
            | _, None -> Map.empty
            | false, Some claim ->
                (CaseFields.groupValues claim.Registration claim.Finance.Decision
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
