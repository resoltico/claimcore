namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

/// Request parsing adapts value-module failures to the field-specific diagnostic target.
module internal Validation =
    type Facts =
        {
            IncidentDate: DateOnly
            IncidentNotificationDate: DateOnly
            IncidentCountry: string
            ClaimantName: string
            InsurerName: string
            ClaimedAmount: Money
        }

    type Decision =
        {
            PaymentDecisionDate: DateOnly
            PayableAmount: Money
        }

    let invalid field violation =
        Error(Refusal.InvalidInput(field, violation))

    let private at target =
        Result.mapError (fun violation -> Refusal.InvalidInput(target, violation))

    let text target value =
        let parsed =
            match target with
            | InputTarget.Field BusinessField.IncidentCountry ->
                CountryCode.parse value |> Result.map CountryCode.format
            | InputTarget.Field BusinessField.CaseReference ->
                CaseReference.parse value |> Result.map CaseReference.format
            | InputTarget.Field BusinessField.ClaimantName
            | InputTarget.Field BusinessField.InsurerName ->
                PartyName.parse value |> Result.map PartyName.format
            | InputTarget.Reason -> Reason.parse value |> Result.map Reason.format
            | _ -> invalidArg (nameof target) "Expected a text field or reason."

        parsed |> at target

    let admission target value =
        let admitted =
            match target with
            | InputTarget.Reason ->
                Reason.parse value |> Result.bind Reason.admit |> Result.map ignore
            | InputTarget.Field BusinessField.ClaimantName
            | InputTarget.Field BusinessField.InsurerName ->
                PartyName.parse value |> Result.bind PartyName.admit |> Result.map ignore
            | InputTarget.Field BusinessField.CaseReference
            | InputTarget.Field BusinessField.IncidentCountry -> Ok()
            | _ -> invalidArg (nameof target) "Expected a text field or reason."

        admitted |> at target

    let assertedText previous (facts: Facts) =
        let changed field before after =
            if before = Some after then Ok() else admission field after

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
        CalendarDate.parse value |> Result.map _.Value |> at (InputTarget.Field input)

    let onOrBefore field (first: DateOnly) (last: DateOnly) =
        if first <= last then
            Ok()
        else
            invalid field (Violation.ChronologicalOrder)

    let notFuture field today value =
        if value <= today then
            Ok()
        else
            invalid field (Violation.FutureDate)

    let canonicalAmount = Money.canonicalAmount

    let amount input currencyInput rawAmount rawCurrency =
        Money.parse rawAmount rawCurrency
        |> Result.mapError (function
            | MoneyViolation.Amount violation ->
                Refusal.InvalidInput(InputTarget.Field input, violation)
            | MoneyViolation.Currency violation ->
                Refusal.InvalidInput(InputTarget.Field currencyInput, violation))

    let registration (input: Registration) =
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
                text (InputTarget.Field BusinessField.IncidentCountry) input.IncidentCountry

            let! claimant =
                text (InputTarget.Field BusinessField.ClaimantName) input.ClaimantName

            let! insurer = text (InputTarget.Field BusinessField.InsurerName) input.InsurerName

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

    let decision (input: ClaimCore.Domain.Decision) =
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

    let amountText = Money.format

    let dateText value =
        CalendarDate.ofDate value |> CalendarDate.format
