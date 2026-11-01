namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Partial assertions merge only with the locked accepted state. Reaffirmation carries no new date.
module internal CaseCorrections =
    let private decisionChoice progress choice =
        match choice, PaymentProgress.decision progress with
        | Change.Keep, current -> Ok current
        | _, None -> Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue)
        | Change.Update update, Some current -> Ok(Some(DecisionUpdate.apply update current))
        | Change.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        match choice, PaymentProgress.payment progress with
        | PaymentChange.Keep, current -> Ok current
        | _, None -> Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue)
        | PaymentChange.Reaffirm, Some current -> Ok(Some current)
        | PaymentChange.Replace replacement, Some _ -> Ok(Some replacement)
        | PaymentChange.Clear, Some _ -> Ok None

    let availableActions progress =
        function
        | RecordGroup.Registration -> Amendment.actions
        | RecordGroup.Decision ->
            if (PaymentProgress.decision progress).IsSome then
                Change.actions
            else
                [ CorrectionGroupAction.Keep ]
        | RecordGroup.Payment ->
            if (PaymentProgress.payment progress).IsSome then
                PaymentChange.actions
            else
                [ CorrectionGroupAction.Keep ]

    let private paidAcknowledgement progress (correction: ValidCorrection) =
        match progress, correction.Decision, correction.Payment with
        | PaymentProgress.Paid _, Change.Clear, payment when payment <> PaymentChange.Clear ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentClearRequired
        | PaymentProgress.Paid _, Change.Update _, PaymentChange.Keep ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentAcknowledgementRequired
        | _ -> Ok()

    let assertedValues (correction: ValidCorrection) =
        [
            match correction.Registration with
            | Amendment.Keep -> ()
            | Amendment.Update update -> yield! RegistrationUpdate.values update
            match correction.Decision with
            | Change.Update update -> yield! DecisionUpdate.values update
            | _ -> ()
            match correction.Payment with
            | PaymentChange.Replace payment -> yield! Payment.values payment
            | _ -> ()
        ]

    let private proposedProgress progress (correction: ValidCorrection) =
        Admission.map2
            (fun decision payment -> decision, payment)
            (decisionChoice progress correction.Decision)
            (paymentChoice progress correction.Payment)

    /// Invalid registration chronology cannot hide an independent assertion or cross-group issue.
    let admission today (before: Claim) (correction: ValidCorrection) =
        let members = assertedValues correction

        let notification =
            match correction.Registration with
            | Amendment.Keep -> Registration.incidentNotificationDate before.Facts
            | Amendment.Update update ->
                defaultArg
                    update.NotificationDate
                    (Registration.incidentNotificationDate before.Facts)

        Admission.collect
            [
                StateValidation.assertionsAgainst today (Some before) members
                proposedProgress before.Progress correction
                |> Result.bind (fun (decision, payment) ->
                    StateValidation.crossProgress
                        (members |> List.map fst |> Set.ofList)
                        notification
                        decision
                        payment)
            ]

    let proposedGroups facts progress (correction: ValidCorrection) =
        let facts =
            match correction.Registration with
            | Amendment.Keep -> Ok facts
            | Amendment.Update update ->
                RegistrationUpdate.apply update facts |> Validation.registrationMerge

        Admission.map2
            (fun facts (decision, payment) -> facts, decision, payment)
            facts
            (proposedProgress progress correction)

    let apply facts progress (correction: ValidCorrection) =
        result {
            let! nextFacts, nextDecision, nextPayment =
                Admission.map2
                    (fun groups () -> groups)
                    (proposedGroups facts progress correction)
                    (paidAcknowledgement progress correction)

            let! nextProgress =
                PaymentProgress.fromGroups nextDecision nextPayment
                |> Result.mapError (fun () -> Refusal.Case CaseRefusal.DecisionRequired)

            if nextFacts = facts && nextProgress = progress then
                return! Error(Refusal.Case CaseRefusal.CorrectionNoChanges)
            else
                return nextFacts, nextProgress
        }
