namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Correction choices are parsed once; KEEP takes typed accepted state under the caller's lock.
module internal CaseCorrections =
    let private requireChange (correction: CaseCorrection) =
        match correction.Registration, correction.Decision, correction.Payment with
        | RegistrationCorrection.Keep, DecisionCorrection.Keep, PaymentCorrection.Keep ->
            Error DomainError.CorrectionNoChanges
        | _ -> Ok()

    let private registrationInput =
        function
        | RegistrationCorrection.Keep -> Ok ValidatedRegistrationCorrection.Keep
        | RegistrationCorrection.Replace value ->
            Validation.registration value
            |> Result.map ValidatedRegistrationCorrection.Replace

    let private decisionInput =
        function
        | DecisionCorrection.Keep -> Ok ValidatedDecisionCorrection.Keep
        | DecisionCorrection.Clear -> Ok ValidatedDecisionCorrection.Clear
        | DecisionCorrection.Replace value ->
            Validation.decision value |> Result.map ValidatedDecisionCorrection.Replace

    let private paymentInput =
        function
        | PaymentCorrection.Keep -> Ok ValidatedPaymentCorrection.Keep
        | PaymentCorrection.Clear -> Ok ValidatedPaymentCorrection.Clear
        | PaymentCorrection.Replace value ->
            Validation.date InputTarget.PaymentDate value
            |> Result.map ValidatedPaymentCorrection.Replace

    let parse (correction: CaseCorrection) =
        result {
            do! requireChange correction
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
        | _, None -> Error DomainError.CorrectionRequiresExistingValue
        | ValidatedDecisionCorrection.Replace replacement, Some _ -> Ok(Some replacement)
        | ValidatedDecisionCorrection.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        let current = PaymentProgress.paymentDate progress

        match choice, current with
        | ValidatedPaymentCorrection.Keep, _ -> Ok current
        | _, None -> Error DomainError.CorrectionRequiresExistingValue
        | ValidatedPaymentCorrection.Replace replacement, Some _ -> Ok(Some replacement)
        | ValidatedPaymentCorrection.Clear, Some _ -> Ok None

    let private paidAcknowledgement progress (correction: ValidatedCaseCorrection) =
        match progress, correction.Decision, correction.Payment with
        | PaymentProgress.Paid _, ValidatedDecisionCorrection.Clear, payment when
            payment <> ValidatedPaymentCorrection.Clear
            ->
            Validation.invalidCorrection
                InputTarget.Payment
                CorrectionViolation.PaymentClearRequired
        | PaymentProgress.Paid _,
          ValidatedDecisionCorrection.Replace _,
          ValidatedPaymentCorrection.Keep ->
            Validation.invalidCorrection
                InputTarget.Payment
                CorrectionViolation.PaymentAcknowledgementRequired
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
                return! Error DomainError.CorrectionNoChanges
            else
                return nextFacts, nextProgress
        }
