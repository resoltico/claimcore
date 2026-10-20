namespace ClaimCore.Catalog

/// Closed diagnostic locations. No submitted field name, path, or value can be echoed as a target.
[<RequireQualifiedAccess>]
type InputTarget =
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | OperationId
    | ExpectedRevision
    | Payment
    | Reason

module InputTargets =
    let all =
        [
            InputTarget.IncidentDate, "incidentDate"
            InputTarget.IncidentNotificationDate, "incidentNotificationDate"
            InputTarget.IncidentCountry, "incidentCountry"
            InputTarget.ClaimantName, "claimantName"
            InputTarget.InsurerName, "insurerName"
            InputTarget.ClaimedAmount, "claimedAmount"
            InputTarget.ClaimedCurrency, "claimedCurrency"
            InputTarget.CaseReference, "caseReference"
            InputTarget.PaymentDecisionDate, "paymentDecisionDate"
            InputTarget.PayableAmount, "payableAmount"
            InputTarget.PayableCurrency, "payableCurrency"
            InputTarget.PaymentDate, "paymentDate"
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedRevision, "expectedRevision"
            InputTarget.Payment, "payment"
            InputTarget.Reason, "reason"
        ]

    let token target =
        all |> List.find (fst >> (=) target) |> snd
