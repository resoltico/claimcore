namespace ClaimCore.Domain

open ClaimCore.Contract

open ClaimCore.Domain.ResultFlow

/// Correction choices are parsed once; KEEP takes typed accepted state under the caller's lock.
module internal CaseCorrections =
    let private registrationInput =
        function
        | RegistrationGroup.Keep -> Ok ValidatedRegistrationCorrection.Keep
        | RegistrationGroup.Replace value ->
            Validation.registration value
            |> Result.map ValidatedRegistrationCorrection.Replace

    let private decisionInput =
        function
        | DecisionGroup.Keep -> Ok ValidatedDecisionCorrection.Keep
        | DecisionGroup.Clear -> Ok ValidatedDecisionCorrection.Clear
        | DecisionGroup.Replace value ->
            Validation.decision value |> Result.map ValidatedDecisionCorrection.Replace

    let private paymentInput =
        function
        | PaymentGroup.Keep -> Ok ValidatedPaymentCorrection.Keep
        | PaymentGroup.Clear -> Ok ValidatedPaymentCorrection.Clear
        | PaymentGroup.Replace value ->
            Validation.date DateInput.PaymentDate value.PaymentDate
            |> Result.map ValidatedPaymentCorrection.Replace

    let parse (correction: Correction) : Result<ValidatedCaseCorrection, Refusal> =
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
        | ValidatedDecisionCorrection.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | ValidatedDecisionCorrection.Replace replacement, Some _ -> Ok(Some replacement)
        | ValidatedDecisionCorrection.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        let current = PaymentProgress.paymentDate progress

        match choice, current with
        | ValidatedPaymentCorrection.Keep, _ -> Ok current
        | _, None -> Error Refusal.CorrectionRequiresExistingValue
        | ValidatedPaymentCorrection.Replace replacement, Some _ -> Ok(Some replacement)
        | ValidatedPaymentCorrection.Clear, Some _ -> Ok None

    /// Unary presence advice shares the execution guards; combinations are still decided by apply.
    let availableActions progress group =
        let present =
            match group with
            | CorrectionGroup.Registration -> true
            | CorrectionGroup.Decision ->
                decisionChoice progress ValidatedDecisionCorrection.Clear |> Result.isOk
            | CorrectionGroup.Payment ->
                paymentChoice progress ValidatedPaymentCorrection.Clear |> Result.isOk

        if present then
            ClaimCore.Contract.Protocol.RequestCodecs.correctionActions group
        else
            [ CorrectionGroupAction.Keep ]

    let private paidAcknowledgement progress (correction: ValidatedCaseCorrection) =
        match progress, correction.Decision, correction.Payment with
        | PaymentProgress.Paid _, ValidatedDecisionCorrection.Clear, payment when
            payment <> ValidatedPaymentCorrection.Clear
            ->
            Validation.invalid InputTarget.PaymentGroup Violation.PaymentClearRequired
        | PaymentProgress.Paid _,
          ValidatedDecisionCorrection.Replace _,
          ValidatedPaymentCorrection.Keep ->
            Validation.invalid InputTarget.PaymentGroup Violation.PaymentAcknowledgementRequired
        | _ -> Ok()

    let apply facts progress (correction: ValidatedCaseCorrection) =
        result {
            let nextFacts =
                match correction.Registration with
                | ValidatedRegistrationCorrection.Keep -> facts
                | ValidatedRegistrationCorrection.Replace replacement -> replacement

            let! nextDecision = decisionChoice progress correction.Decision
            let! nextPayment = paymentChoice progress correction.Payment
            do! paidAcknowledgement progress correction
            let! nextProgress = PaymentProgress.fromGroups nextDecision nextPayment

            if nextFacts = facts && nextProgress = progress then
                return! Error Refusal.CorrectionNoChanges
            else
                return nextFacts, nextProgress
        }
