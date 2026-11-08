namespace ClaimCore.Domain

/// Corrections preserve entries and admit both groups against the state used for the action.
module internal CaseCorrections =
    let private decisionChoice progress choice =
        match choice, PaymentProgress.decision progress with
        | Change.Keep, current -> Ok current
        | _, None -> Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue)
        | Change.Update update, Some current -> Ok(Some(DecisionUpdate.apply update current))
        | Change.Clear, Some _ -> Ok None

    let availableActions progress =
        function
        | RecordGroup.Registration -> Amendment.actions
        | RecordGroup.Decision ->
            if (PaymentProgress.decision progress).IsNone then
                [ CorrectionGroupAction.Keep ]
            elif (PaymentProgress.payments progress).IsEmpty then
                Change.actions
            else
                [ CorrectionGroupAction.Keep; CorrectionGroupAction.Update ]
        | RecordGroup.Payment -> []

    let private assertedValues (correction: ValidCorrection) =
        [
            match correction.Registration with
            | Amendment.Keep -> ()
            | Amendment.Update update -> yield! RegistrationUpdate.values update
            match correction.Decision with
            | Change.Update update -> yield! DecisionUpdate.values update
            | _ -> ()
        ]

    let apply today (before: Claim) (correction: ValidCorrection) =
        let members = assertedValues correction

        let facts =
            match correction.Registration with
            | Amendment.Keep -> Ok before.Facts
            | Amendment.Update update ->
                RegistrationUpdate.apply update before.Facts
                |> InputValidation.registrationMerge

        let decision = decisionChoice before.Progress correction.Decision

        let notification =
            match correction.Registration with
            | Amendment.Keep -> Registration.incidentNotificationDate before.Facts
            | Amendment.Update update ->
                defaultArg
                    (RegistrationUpdate.tryValue BusinessField.IncidentNotificationDate update
                     |> Option.map FieldValue.date)
                    (Registration.incidentNotificationDate before.Facts)

        let rules =
            Admission.collect
                [
                    StateValidation.assertionsAgainst today (Some before) members
                    decision
                    |> Result.bind (fun decision ->
                        Admission.collect
                            [
                                StateValidation.decisionChange before.Progress decision
                                StateValidation.crossProgress
                                    (members |> List.map fst |> Set.ofList)
                                    notification
                                    decision
                                    (PaymentProgress.payments before.Progress)
                            ])
                ]

        Admission.map2 (fun facts decision -> facts, decision) facts decision
        |> fun groups -> Admission.map2 (fun groups () -> groups) groups rules
        |> Result.bind (fun (facts, decision) ->
            let progress =
                PaymentProgress.fromGroups decision (PaymentProgress.payments before.Progress)
                |> Result.defaultWith (fun () ->
                    invalidOp "Admitted correction groups form valid financial state.")

            if facts = before.Facts && progress = before.Progress then
                Error(Refusal.Case CaseRefusal.CorrectionNoChanges)
            else
                Ok(facts, progress))
