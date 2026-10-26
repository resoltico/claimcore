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

[<RequireQualifiedAccess>]
type FieldGroup =
    | Identity
    | Registration
    | Decision
    | Payment
    | Status

module FieldGroup =
    let token =
        function
        | FieldGroup.Identity -> "identity"
        | FieldGroup.Registration -> "registration"
        | FieldGroup.Decision -> "decision"
        | FieldGroup.Payment -> "payment"
        | FieldGroup.Status -> "status"

[<RequireQualifiedAccess>]
type ScalarKind =
    | Date
    | Text
    | Country
    | Amount
    | Currency
    | Reference
    | Status
