namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Validates and assembles one complete correction before Claim restores its opaque accepted state.
/// It operates on the current renderable fields so KEEP cannot accept caller-supplied duplicates.
module internal CaseCorrections =
    let private invalid field message = Validation.invalid field message

    let private validateDecisionDate
        today
        (current: CaseFields)
        (choice: CorrectionResolution.DecisionChoice)
        =
        result {
            match choice.Value, choice.Replaced with
            | Some replacement, true ->
                let! existing = CorrectionResolution.decision current

                match existing with
                | Some prior ->
                    do!
                        CorrectionResolution.dateChanged
                            "paymentDecisionDate"
                            today
                            prior.PaymentDecisionDate
                            replacement.PaymentDecisionDate
                | None -> return! Error DomainError.CorrectionRequiresExistingValue
            | _ -> ()
        }

    let private validatePaymentDate
        today
        (current: CaseFields)
        (choice: CorrectionResolution.PaymentChoice)
        =
        result {
            match choice.Value, choice.Replaced, current.PaymentDate with
            | Some replacement, true, Some prior ->
                do! CorrectionResolution.dateChanged "paymentDate" today prior replacement
            | Some _, true, None -> return! Error DomainError.CorrectionRequiresExistingValue
            | _ -> ()
        }

    let private combineDecisionAndPayment
        (current: CaseFields)
        (decisionChoice: CorrectionResolution.DecisionChoice)
        (paymentChoice: CorrectionResolution.PaymentChoice)
        =
        if
            decisionChoice.Cleared
            && current.PaymentDate.IsSome
            && not paymentChoice.Cleared
        then
            invalid "payment" "Clearing a paid decision requires clearing the payment record."
        elif
            decisionChoice.Replaced
            && current.PaymentDate.IsSome
            && not paymentChoice.Replaced
            && not paymentChoice.Cleared
        then
            invalid
                "payment"
                "Replacing a paid decision requires explicit payment reaffirmation or clearing the payment record."
        elif decisionChoice.Value.IsNone && paymentChoice.Value.IsSome then
            invalid "payment" "A payment record requires a complete payment decision."
        else
            Ok()

    let private applyDecision
        (fields: CaseFields)
        (choice: CorrectionResolution.DecisionChoice)
        : CaseFields =
        match choice.Value with
        | None ->
            { fields with
                PaymentDecisionDate = None
                PayableAmount = None
                PayableCurrency = None
            }
        | Some replacement ->
            { fields with
                PaymentDecisionDate = Some replacement.PaymentDecisionDate
                PayableAmount = Some replacement.PayableAmount
                PayableCurrency = Some replacement.PayableCurrency
            }

    let private applyPayment
        (fields: CaseFields)
        (choice: CorrectionResolution.PaymentChoice)
        : CaseFields =
        { fields with
            PaymentDate = choice.Value
        }

    let private validateFinalState (fields: CaseFields) =
        result {
            let! facts = fields |> CorrectionResolution.registration |> Validation.registration
            let! decisionValue = CorrectionResolution.decision fields

            match decisionValue, fields.PaymentDate with
            | None, None -> return ()
            | None, Some _ -> return! invalid "paymentDate" "A payment date requires a decision."
            | Some value, payment ->
                let! parsedDecision = Validation.decision value

                do!
                    Validation.onOrBefore
                        "paymentDecisionDate"
                        facts.NotificationDate
                        parsedDecision.Date

                match payment with
                | None -> return ()
                | Some rawDate ->
                    let! paid = Validation.date "paymentDate" rawDate
                    do! Validation.onOrBefore "paymentDate" parsedDecision.Date paid

                    if parsedDecision.Payable.Value = 0M then
                        return! Error DomainError.ZeroDecisionCannotBePaid
        }

    let private logicalState (fields: CaseFields) =
        result {
            let! facts = fields |> CorrectionResolution.registration |> Validation.registration
            let! decisionValue = CorrectionResolution.decision fields

            let! parsedDecision =
                match decisionValue with
                | None -> Ok None
                | Some value -> Validation.decision value |> Result.map Some

            let! paid =
                match fields.PaymentDate with
                | None -> Ok None
                | Some value -> Validation.date "paymentDate" value |> Result.map Some

            return facts, parsedDecision, paid, fields.Status
        }

    let private requireChange (correction: CaseCorrection) =
        match correction.Registration, correction.Decision, correction.Payment with
        | RegistrationCorrection.Keep, DecisionCorrection.Keep, PaymentCorrection.Keep ->
            Error DomainError.CorrectionNoChanges
        | _ -> Ok()

    let private validateRegistrationInput =
        function
        | RegistrationCorrection.Keep -> Ok()
        | RegistrationCorrection.Replace value -> Validation.registration value |> Result.map ignore

    let private validateDecisionInput =
        function
        | DecisionCorrection.Keep
        | DecisionCorrection.Clear -> Ok()
        | DecisionCorrection.Replace value -> Validation.decision value |> Result.map ignore

    let private validatePaymentInput =
        function
        | PaymentCorrection.Keep
        | PaymentCorrection.Clear -> Ok()
        | PaymentCorrection.Replace value ->
            Validation.date "paymentDate" value |> Result.map ignore

    let validate (correction: CaseCorrection) =
        result {
            do! requireChange correction
            do! validateRegistrationInput correction.Registration
            do! validateDecisionInput correction.Decision
            do! validatePaymentInput correction.Payment
        }

    let apply today (current: CaseFields) (correction: CaseCorrection) =
        result {
            do! validate correction

            let! replacementRegistration =
                CorrectionResolution.registrationChoice today current correction.Registration

            let! replacementDecision =
                CorrectionResolution.decisionChoice current correction.Decision

            let! replacementPayment =
                CorrectionResolution.paymentChoice current correction.Payment

            do! validateDecisionDate today current replacementDecision
            do! validatePaymentDate today current replacementPayment
            do! combineDecisionAndPayment current replacementDecision replacementPayment

            let candidate =
                current
                |> fun fields ->
                    { fields with
                        IncidentDate = replacementRegistration.IncidentDate
                        IncidentNotificationDate = replacementRegistration.IncidentNotificationDate
                        IncidentCountry = replacementRegistration.IncidentCountry
                        ClaimantName = replacementRegistration.ClaimantName
                        InsurerName = replacementRegistration.InsurerName
                        ClaimedAmount = replacementRegistration.ClaimedAmount
                        ClaimedCurrency = replacementRegistration.ClaimedCurrency
                    }
                |> fun fields -> applyDecision fields replacementDecision
                |> fun fields -> applyPayment fields replacementPayment

            do! validateFinalState candidate
            let! before = logicalState current
            let! after = logicalState candidate

            if before = after then
                return! Error DomainError.CorrectionNoChanges
            else
                return candidate
        }
