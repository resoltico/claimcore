namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Request parsing adapts value-module failures to the field-specific diagnostic target.
module internal Validation =
    let invalid field violation =
        Error(Refusal.InvalidInput(field, violation))

    let private at target =
        Result.mapError (fun violation -> Refusal.InvalidInput(target, violation))

    let assertedText previous (facts: Registration) =
        let changed field before after =
            if before = Some after then
                Ok()
            else
                PartyName.admit after |> Result.map ignore |> at field

        result {
            do!
                changed
                    (InputTarget.Field BusinessField.ClaimantName)
                    (previous |> Option.map _.ClaimantName)
                    facts.ClaimantName

            do!
                changed
                    (InputTarget.Field BusinessField.InsurerName)
                    (previous |> Option.map _.InsurerName)
                    facts.InsurerName
        }

    let date input value =
        CalendarDate.parse value |> at (InputTarget.Field input)

    let onOrBefore field (first: CalendarDate) (last: CalendarDate) =
        if first <= last then
            Ok()
        else
            invalid field (Violation.ChronologicalOrder)

    let notFuture field today value =
        CalendarDate.admit today value |> Result.map ignore |> at field


    let amount input currencyInput rawAmount rawCurrency =
        Money.parse rawAmount rawCurrency
        |> Result.mapError (function
            | MoneyViolation.Amount violation ->
                Refusal.InvalidInput(InputTarget.Field input, violation)
            | MoneyViolation.Currency violation ->
                Refusal.InvalidInput(InputTarget.Field currencyInput, violation))

    let registration (input: RegistrationInput) : Result<Registration, Refusal> =
        result {
            let! incident = date BusinessField.IncidentDate input.IncidentDate

            let! notification =
                date BusinessField.IncidentNotificationDate input.IncidentNotificationDate

            do!
                onOrBefore
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    incident
                    notification

            let! country =
                CountryCode.parse input.IncidentCountry
                |> at (InputTarget.Field BusinessField.IncidentCountry)

            let! claimant =
                PartyName.parse input.ClaimantName
                |> at (InputTarget.Field BusinessField.ClaimantName)

            let! insurer =
                PartyName.parse input.InsurerName
                |> at (InputTarget.Field BusinessField.InsurerName)

            let! claimed =
                amount
                    BusinessField.ClaimedAmount
                    BusinessField.ClaimedCurrency
                    input.ClaimedAmount
                    input.ClaimedCurrency

            return
                {
                    IncidentDate = incident
                    IncidentNotificationDate = notification
                    IncidentCountry = country
                    ClaimantName = claimant
                    InsurerName = insurer
                    ClaimedAmount = claimed
                }
        }

    let decision (input: DecisionInput) : Result<Decision, Refusal> =
        result {
            let! decisionDate = date BusinessField.PaymentDecisionDate input.PaymentDecisionDate

            let! payable =
                amount
                    BusinessField.PayableAmount
                    BusinessField.PayableCurrency
                    input.PayableAmount
                    input.PayableCurrency

            return
                {
                    PaymentDecisionDate = decisionDate
                    PayableAmount = payable
                }
        }
