namespace ClaimCore.Domain

open ClaimCore.Catalog

open ClaimCore.Domain.ResultFlow

/// Correction choices are parsed once; KEEP takes typed accepted state under the caller's lock.
module internal CaseCorrections =
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
        | _, None -> Error CaseRefusal.CorrectionRequiresExistingValue
        | ValidatedDecisionCorrection.Replace replacement, Some _ -> Ok(Some replacement)
        | ValidatedDecisionCorrection.Clear, Some _ -> Ok None

    let private paymentChoice progress choice =
        let current = PaymentProgress.paymentDate progress

        match choice, current with
        | ValidatedPaymentCorrection.Keep, _ -> Ok current
        | _, None -> Error CaseRefusal.CorrectionRequiresExistingValue
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

        let definition =
            match
                (CommandDefinitions.forKind (CaseActKind.Business BusinessActKind.CorrectCase))
                    .Inputs
            with
            | CommandInputShape.CorrectionGroups groups ->
                groups |> List.find (fun value -> value.Name = group)
            | _ -> invalidOp "Correction requires explicit groups."

        if present then
            definition.Actions
        else
            [ CorrectionGroupAction.Keep ]

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
                return! Error CaseRefusal.CorrectionNoChanges
            else
                return nextFacts, nextProgress
        }
