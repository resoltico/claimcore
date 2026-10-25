namespace ClaimCore.Contract

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: string
        Status: CaseStatus
        Registration: Registration
        Decision: Decision option
        Payment: Payment option
    }

module CaseFields =
    let value field (fields: CaseFields) =
        match field with
        | BusinessField.CaseReference -> Some fields.CaseReference
        | BusinessField.Status -> Some(CaseStatuses.token fields.Status)
        | BusinessField.IncidentDate -> Some fields.Registration.IncidentDate
        | BusinessField.IncidentNotificationDate ->
            Some fields.Registration.IncidentNotificationDate
        | BusinessField.IncidentCountry -> Some fields.Registration.IncidentCountry
        | BusinessField.ClaimantName -> Some fields.Registration.ClaimantName
        | BusinessField.InsurerName -> Some fields.Registration.InsurerName
        | BusinessField.ClaimedAmount -> Some fields.Registration.ClaimedAmount
        | BusinessField.ClaimedCurrency -> Some fields.Registration.ClaimedCurrency
        | BusinessField.PaymentDecisionDate -> fields.Decision |> Option.map _.PaymentDecisionDate
        | BusinessField.PayableAmount -> fields.Decision |> Option.map _.PayableAmount
        | BusinessField.PayableCurrency -> fields.Decision |> Option.map _.PayableCurrency
        | BusinessField.PaymentDate -> fields.Payment |> Option.map _.PaymentDate

[<RequireQualifiedAccess>]
type CaseView =
    | Live of fields: CaseFields * disposition: Disposition
    | Erased

type CaseSnapshot =
    {
        Reference: string
        Revision: int64
        View: CaseView
    }
