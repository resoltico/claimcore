namespace ClaimCore.Contract

/// Business fields. Technical revision, receipt and UI metadata are separate.
/// Optional fields remain absent until the operator records a decision or payment.
type CaseFields =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
        CaseReference: string
        PaymentDecisionDate: string option
        PayableAmount: string option
        PayableCurrency: string option
        PaymentDate: string option
        Status: CaseStatus
    }

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
