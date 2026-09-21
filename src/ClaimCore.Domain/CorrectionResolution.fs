namespace ClaimCore.Domain

open ClaimCore.Domain.ResultFlow

/// Resolves correction-group choices from accepted fields. KEEP always takes its value from the
/// current case, never from duplicated caller input.
module internal CorrectionResolution =
    type DecisionChoice =
        {
            Value: DecisionInput option
            Replaced: bool
            Cleared: bool
        }

    type PaymentChoice =
        {
            Value: string option
            Replaced: bool
            Cleared: bool
        }

    let private invalid field message = Validation.invalid field message

    let registration (fields: CaseFields) : RegistrationInput =
        {
            IncidentDate = fields.IncidentDate
            IncidentNotificationDate = fields.IncidentNotificationDate
            IncidentCountry = fields.IncidentCountry
            ClaimantName = fields.ClaimantName
            InsurerName = fields.InsurerName
            ClaimedAmount = fields.ClaimedAmount
            ClaimedCurrency = fields.ClaimedCurrency
        }

    let decision (fields: CaseFields) : Result<DecisionInput option, DomainError> =
        match fields.PaymentDecisionDate, fields.PayableAmount, fields.PayableCurrency with
        | None, None, None -> Ok None
        | Some date, Some amount, Some currency ->
            Ok(
                Some
                    {
                        PaymentDecisionDate = date
                        PayableAmount = amount
                        PayableCurrency = currency
                    }
            )
        | _ ->
            invalid
                InputTarget.PaymentDecisionDate
                (InputViolation.Correction CorrectionViolation.CompleteDecisionRequired)

    let dateChanged field today before after =
        result {
            let! prior = Validation.date field before
            let! candidate = Validation.date field after

            if prior <> candidate then
                do! Validation.notFuture field today candidate
        }

    let private validateRegistrationDates
        today
        (current: CaseFields)
        (replacement: RegistrationInput)
        =
        result {
            do!
                dateChanged
                    InputTarget.IncidentDate
                    today
                    current.IncidentDate
                    replacement.IncidentDate

            do!
                dateChanged
                    InputTarget.IncidentNotificationDate
                    today
                    current.IncidentNotificationDate
                    replacement.IncidentNotificationDate
        }

    let registrationChoice today (current: CaseFields) (correction: RegistrationCorrection) =
        match correction with
        | RegistrationCorrection.Keep -> Ok(registration current)
        | RegistrationCorrection.Replace replacement ->
            result {
                do! Validation.registration replacement |> Result.map ignore
                do! validateRegistrationDates today current replacement
                return replacement
            }

    let decisionChoice
        (current: CaseFields)
        (correction: DecisionCorrection)
        : Result<DecisionChoice, DomainError> =
        result {
            let! existing = decision current

            match correction with
            | DecisionCorrection.Keep ->
                return
                    {
                        Value = existing
                        Replaced = false
                        Cleared = false
                    }
            | DecisionCorrection.Replace replacement ->
                match existing with
                | None -> return! Error DomainError.CorrectionRequiresExistingValue
                | Some _ ->
                    do! Validation.decision replacement |> Result.map ignore

                    return
                        {
                            Value = Some replacement
                            Replaced = true
                            Cleared = false
                        }
            | DecisionCorrection.Clear ->
                match existing with
                | None -> return! Error DomainError.CorrectionRequiresExistingValue
                | Some _ ->
                    return
                        {
                            Value = None
                            Replaced = false
                            Cleared = true
                        }
        }

    let paymentChoice
        (current: CaseFields)
        (correction: PaymentCorrection)
        : Result<PaymentChoice, DomainError> =
        match correction with
        | PaymentCorrection.Keep ->
            Ok
                {
                    Value = current.PaymentDate
                    Replaced = false
                    Cleared = false
                }
        | PaymentCorrection.Replace replacement ->
            match current.PaymentDate with
            | None -> Error DomainError.CorrectionRequiresExistingValue
            | Some _ ->
                Validation.date InputTarget.PaymentDate replacement
                |> Result.map (fun _ ->
                    {
                        Value = Some replacement
                        Replaced = true
                        Cleared = false
                    })
        | PaymentCorrection.Clear ->
            match current.PaymentDate with
            | None -> Error DomainError.CorrectionRequiresExistingValue
            | Some _ ->
                Ok
                    {
                        Value = None
                        Replaced = false
                        Cleared = true
                    }
