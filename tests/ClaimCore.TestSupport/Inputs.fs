module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.Application
open Expecto

/// Builders construct inputs only. Expected outputs and oracle values must stay literal.
let registration: RegistrationInput =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = "Synthetic Party"
        InsurerName = "Synthetic Insurer"
        ClaimedAmount = "1000.00"
        ClaimedCurrency = "EUR"
    }

let registrationOn incident notification =
    { registration with
        IncidentDate = incident
        IncidentNotificationDate = notification
    }

let decision: DecisionInput =
    {
        PaymentDecisionDate = "2026-08-15"
        PayableAmount = "750.00"
        PayableCurrency = "EUR"
    }

let payment: PaymentInput = { PaymentDate = "2026-08-20" }

let correction: AuthoredCorrection =
    {
        Registration = Change.Keep
        Decision = Change.Keep
        Payment = Change.Keep
    }

let principal: Principal =
    {
        Kind = "HUMAN"
        Issuer = "https://issuer.example/"
        Subject = "synthetic-owner"
    }

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let date value = CalendarDate.parse value |> accepted
let country value = CountryCode.parse value |> accepted
let name value = PartyName.parse value |> accepted
let reference value = CaseReference.parse value |> accepted
let money amount currency = Money.parse amount currency |> accepted

let admittedRegistration (input: RegistrationInput) : Registration =
    {
        IncidentDate = date input.IncidentDate
        IncidentNotificationDate = date input.IncidentNotificationDate
        IncidentCountry = country input.IncidentCountry
        ClaimantName = name input.ClaimantName
        InsurerName = name input.InsurerName
        ClaimedAmount = money input.ClaimedAmount input.ClaimedCurrency
    }

let admittedDecision (input: DecisionInput) : Decision =
    {
        PaymentDecisionDate = date input.PaymentDecisionDate
        PayableAmount = money input.PayableAmount input.PayableCurrency
    }

let admittedPayment (input: PaymentInput) : Payment =
    { PaymentDate = date input.PaymentDate }


let authorityRequest operationId target act : ClaimCore.Domain.AuthorityRequest =
    AuthorityRequests.input operationId target act
    |> Result.defaultWith (fun _ -> failtest "Owner provisioning is not an authored test input.")
