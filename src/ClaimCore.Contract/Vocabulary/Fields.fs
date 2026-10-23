namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type BusinessField =
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
    | Status

module BusinessField =
    let all =
        [
            BusinessField.IncidentDate, "incidentDate"
            BusinessField.IncidentNotificationDate, "incidentNotificationDate"
            BusinessField.IncidentCountry, "incidentCountry"
            BusinessField.ClaimantName, "claimantName"
            BusinessField.InsurerName, "insurerName"
            BusinessField.ClaimedAmount, "claimedAmount"
            BusinessField.ClaimedCurrency, "claimedCurrency"
            BusinessField.CaseReference, "caseReference"
            BusinessField.PaymentDecisionDate, "paymentDecisionDate"
            BusinessField.PayableAmount, "payableAmount"
            BusinessField.PayableCurrency, "payableCurrency"
            BusinessField.PaymentDate, "paymentDate"
            BusinessField.Status, "status"
        ]

    let token field =
        all |> List.find (fst >> (=) field) |> snd

[<RequireQualifiedAccess>]
type CorrectionGroup =
    | Registration
    | Decision
    | Payment

module CorrectionGroup =
    let token =
        function
        | CorrectionGroup.Registration -> "registration"
        | CorrectionGroup.Decision -> "decision"
        | CorrectionGroup.Payment -> "payment"

[<RequireQualifiedAccess>]
type InputField =
    | Business of BusinessField
    | Reason

module InputField =
    let token =
        function
        | InputField.Business field -> BusinessField.token field
        | InputField.Reason -> "reason"

module TextFormat =
    [<Literal>]
    let MaximumFreeTextBytes = 1024

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

module CorrectionActions =
    let token =
        function
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"
