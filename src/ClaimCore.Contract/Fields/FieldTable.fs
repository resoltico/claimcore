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

type FieldRow =
    {
        Field: BusinessField
        Key: string
        Group: FieldGroup
        Kind: ScalarKind
        Optional: bool
    }

module FieldTable =
    let private row field key group kind optional =
        {
            Field = field
            Key = key
            Group = group
            Kind = kind
            Optional = optional
        }

    let rows =
        [
            row
                BusinessField.IncidentDate
                "incidentDate"
                FieldGroup.Registration
                ScalarKind.Date
                false
            row
                BusinessField.IncidentNotificationDate
                "incidentNotificationDate"
                FieldGroup.Registration
                ScalarKind.Date
                false
            row
                BusinessField.IncidentCountry
                "incidentCountry"
                FieldGroup.Registration
                ScalarKind.Country
                false
            row
                BusinessField.ClaimantName
                "claimantName"
                FieldGroup.Registration
                ScalarKind.Text
                false
            row
                BusinessField.InsurerName
                "insurerName"
                FieldGroup.Registration
                ScalarKind.Text
                false
            row
                BusinessField.ClaimedAmount
                "claimedAmount"
                FieldGroup.Registration
                ScalarKind.Amount
                false
            row
                BusinessField.ClaimedCurrency
                "claimedCurrency"
                FieldGroup.Registration
                ScalarKind.Currency
                false
            row
                BusinessField.CaseReference
                "caseReference"
                FieldGroup.Identity
                ScalarKind.Reference
                false
            row
                BusinessField.PaymentDecisionDate
                "paymentDecisionDate"
                FieldGroup.Decision
                ScalarKind.Date
                true
            row
                BusinessField.PayableAmount
                "payableAmount"
                FieldGroup.Decision
                ScalarKind.Amount
                true
            row
                BusinessField.PayableCurrency
                "payableCurrency"
                FieldGroup.Decision
                ScalarKind.Currency
                true
            row BusinessField.PaymentDate "paymentDate" FieldGroup.Payment ScalarKind.Date true
            row BusinessField.Status "status" FieldGroup.Status ScalarKind.Status false
        ]

    let vocabulary =
        rows |> List.map (fun row -> row.Field, row.Key) |> Vocabulary.define "field"

    let key = Vocabulary.token vocabulary

    let forField field =
        rows |> List.find (fun row -> row.Field = field)

    let labelId field =
        Vocabulary.messageKey vocabulary field "label"

    let meaningId field =
        Vocabulary.messageKey vocabulary field "meaning"

module BusinessField =
    let all = FieldTable.rows |> List.map (fun row -> row.Field, row.Key)
    let token = FieldTable.key
