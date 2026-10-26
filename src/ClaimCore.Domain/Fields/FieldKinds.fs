namespace ClaimCore.Domain

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
type FieldPlacement =
    | TopLevel
    | Grouped of RecordGroup

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | Group of RecordGroup
    | Reason
    | PrincipalIssuer
    | PrincipalSubject
    | Limit
    | Cursor
