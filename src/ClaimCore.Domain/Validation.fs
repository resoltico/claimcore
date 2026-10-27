namespace ClaimCore.Domain

/// Request parsing adapts independent value failures to their typed input identities.
module internal Validation =
    let invalid field violation =
        Error(Refusal.invalidInput (field, violation))

    let private at target =
        Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let assertedText previous (facts: Registration) =
        let changed field before after =
            if before = Some after then
                Ok()
            else
                PartyName.admit after |> Result.map ignore |> at field

        Admission.collect
            [
                changed
                    (InputTarget.Field BusinessField.ClaimantName)
                    (previous |> Option.map _.ClaimantName)
                    facts.ClaimantName
                changed
                    (InputTarget.Field BusinessField.InsurerName)
                    (previous |> Option.map _.InsurerName)
                    facts.InsurerName
            ]

    let date input value =
        CalendarDate.parse value |> at (InputTarget.Field input)

    let onOrBefore field (first: CalendarDate) (last: CalendarDate) =
        if first <= last then
            Ok()
        else
            invalid field Violation.ChronologicalOrder

    let notFuture field today value =
        CalendarDate.admit today value |> Result.map ignore |> at field

    let amount input currencyInput rawAmount rawCurrency =
        Money.parseAll rawAmount rawCurrency
        |> Result.mapError (fun errors ->
            errors
            |> List.map (function
                | MoneyViolation.Amount violation ->
                    {
                        Target = InputTarget.Field input
                        Violation = violation
                    }
                | MoneyViolation.Currency violation ->
                    {
                        Target = InputTarget.Field currencyInput
                        Violation = violation
                    })
            |> InputIssues.ofList
            |> Option.get
            |> Refusal.InvalidInput)

    let registration (input: RegistrationInput) : Result<Registration, Refusal> =
        let dates =
            Admission.map2
                (fun incident notification -> incident, notification)
                (date BusinessField.IncidentDate input.IncidentDate)
                (date BusinessField.IncidentNotificationDate input.IncidentNotificationDate)
            |> Result.bind (fun (incident, notification) ->
                onOrBefore
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    incident
                    notification
                |> Result.map (fun () -> incident, notification))

        let location =
            CountryCode.parse input.IncidentCountry
            |> at (InputTarget.Field BusinessField.IncidentCountry)

        let parties =
            Admission.map2
                (fun claimant insurer -> claimant, insurer)
                (PartyName.parse input.ClaimantName
                 |> at (InputTarget.Field BusinessField.ClaimantName))
                (PartyName.parse input.InsurerName
                 |> at (InputTarget.Field BusinessField.InsurerName))

        let money =
            amount
                BusinessField.ClaimedAmount
                BusinessField.ClaimedCurrency
                input.ClaimedAmount
                input.ClaimedCurrency

        Admission.map2
            (fun ((incident, notification), country) ((claimant, insurer), money) ->
                {
                    IncidentDate = incident
                    IncidentNotificationDate = notification
                    IncidentCountry = country
                    ClaimantName = claimant
                    InsurerName = insurer
                    ClaimedAmount = money
                })
            (Admission.map2 (fun dates country -> dates, country) dates location)
            (Admission.map2 (fun parties money -> parties, money) parties money)

    let decision (input: DecisionInput) : Result<Decision, Refusal> =
        Admission.map2
            (fun date money ->
                {
                    PaymentDecisionDate = date
                    PayableAmount = money
                })
            (date BusinessField.PaymentDecisionDate input.PaymentDecisionDate)
            (amount
                BusinessField.PayableAmount
                BusinessField.PayableCurrency
                input.PayableAmount
                input.PayableCurrency)
