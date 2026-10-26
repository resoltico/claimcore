namespace ClaimCore.Domain


open ClaimCore.Domain.ResultFlow

/// Correction choices are parsed once; KEEP takes typed accepted state under the caller's lock.
module internal CaseCorrections =
    let private registrationInput =
        function
        | RegistrationGroup.Keep -> Ok RegistrationChange.Keep
        | RegistrationGroup.Replace value ->
            Validation.registration value |> Result.map RegistrationChange.Replace

    let private decisionInput =
        function
        | DecisionGroup.Keep -> Ok DecisionChange.Keep
        | DecisionGroup.Clear -> Ok DecisionChange.Clear
        | DecisionGroup.Replace value ->
            Validation.decision value |> Result.map DecisionChange.Replace

    let private paymentInput =
        function
        | PaymentGroup.Keep -> Ok PaymentChange.Keep
        | PaymentGroup.Clear -> Ok PaymentChange.Clear
        | PaymentGroup.Replace value ->
            Validation.date BusinessField.PaymentDate value.PaymentDate
            |> Result.map PaymentChange.Replace

    let parse (correction: CorrectionInput) : Result<CaseCorrection, Refusal> =
        result {
            let! registration = registrationInput correction.Registration
            let! decision = decisionInput correction.Decision
            let! payment = paymentInput correction.Payment

            return
                {
                    Registration = registration
                    Decision = decision
                    Payment = payment
                }
        }

    let private decisionChoice progress choice =
        let current = PaymentProgress.decision progress

        match choice, current with
        | DecisionChange.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | DecisionChange.Replace replacement, Some _ -> Ok(Some replacement)
        | DecisionChange.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        let current = PaymentProgress.paymentDate progress

        match choice, current with
        | PaymentChange.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | PaymentChange.Replace replacement, Some _ -> Ok(Some replacement)
        | PaymentChange.Clear, Some _ -> Ok None

    /// Unary presence advice shares the execution guards; combinations are still decided by apply.
    let availableActions progress group =
        let present =
            match group with
            | RecordGroup.Registration -> true
            | RecordGroup.Decision -> decisionChoice progress DecisionChange.Clear |> Result.isOk
            | RecordGroup.Payment -> paymentChoice progress PaymentChange.Clear |> Result.isOk

        if present then
            CorrectionActions.forGroup group
        else
            [ CorrectionGroupAction.Keep ]

    let private paidAcknowledgement progress (correction: CaseCorrection) =
        match progress, correction.Decision, correction.Payment with
        | PaymentProgress.Paid _, DecisionChange.Clear, payment when payment <> PaymentChange.Clear ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentClearRequired
        | PaymentProgress.Paid _, DecisionChange.Replace _, PaymentChange.Keep ->
            Validation.invalid
                (InputTarget.Group RecordGroup.Payment)
                Violation.PaymentAcknowledgementRequired
        | _ -> Ok()

    let apply facts progress (correction: CaseCorrection) =
        result {
            let nextFacts =
                match correction.Registration with
                | RegistrationChange.Keep -> facts
                | RegistrationChange.Replace replacement -> replacement

            let! nextDecision = decisionChoice progress correction.Decision
            let! nextPayment = paymentChoice progress correction.Payment
            do! paidAcknowledgement progress correction
            let! nextProgress = PaymentProgress.fromGroups nextDecision nextPayment

            if nextFacts = facts && nextProgress = progress then
                return! Error Refusal.CorrectionNoChanges
            else
                return nextFacts, nextProgress
        }
