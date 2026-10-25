namespace ClaimCore.Contract

[<NoEquality; NoComparison>]
type FieldRow =
    {
        Field: BusinessField
        Key: string
        Group: FieldGroup
        Kind: ScalarKind
        Optional: bool
        AcceptedValue: CaseFields -> string option
    }

module FieldTable =
    let private row field key group kind optional read =
        {
            Field = field
            Key = key
            Group = group
            Kind = kind
            Optional = optional
            AcceptedValue = read
        }

    let rows =
        [
            row
                BusinessField.IncidentDate
                "incidentDate"
                FieldGroup.Registration
                ScalarKind.Date
                false
                (fun fields -> Some fields.Registration.IncidentDate)
            row
                BusinessField.IncidentNotificationDate
                "incidentNotificationDate"
                FieldGroup.Registration
                ScalarKind.Date
                false
                (fun fields -> Some fields.Registration.IncidentNotificationDate)
            row
                BusinessField.IncidentCountry
                "incidentCountry"
                FieldGroup.Registration
                ScalarKind.Country
                false
                (fun fields -> Some fields.Registration.IncidentCountry)
            row
                BusinessField.ClaimantName
                "claimantName"
                FieldGroup.Registration
                ScalarKind.Text
                false
                (fun fields -> Some fields.Registration.ClaimantName)
            row
                BusinessField.InsurerName
                "insurerName"
                FieldGroup.Registration
                ScalarKind.Text
                false
                (fun fields -> Some fields.Registration.InsurerName)
            row
                BusinessField.ClaimedAmount
                "claimedAmount"
                FieldGroup.Registration
                ScalarKind.Amount
                false
                (fun fields -> Some fields.Registration.ClaimedAmount)
            row
                BusinessField.ClaimedCurrency
                "claimedCurrency"
                FieldGroup.Registration
                ScalarKind.Currency
                false
                (fun fields -> Some fields.Registration.ClaimedCurrency)
            row
                BusinessField.CaseReference
                "caseReference"
                FieldGroup.Identity
                ScalarKind.Reference
                false
                (fun fields -> Some fields.CaseReference)
            row
                BusinessField.PaymentDecisionDate
                "paymentDecisionDate"
                FieldGroup.Decision
                ScalarKind.Date
                true
                (fun fields -> fields.Decision |> Option.map _.PaymentDecisionDate)
            row
                BusinessField.PayableAmount
                "payableAmount"
                FieldGroup.Decision
                ScalarKind.Amount
                true
                (fun fields -> fields.Decision |> Option.map _.PayableAmount)
            row
                BusinessField.PayableCurrency
                "payableCurrency"
                FieldGroup.Decision
                ScalarKind.Currency
                true
                (fun fields -> fields.Decision |> Option.map _.PayableCurrency)
            row
                BusinessField.PaymentDate
                "paymentDate"
                FieldGroup.Payment
                ScalarKind.Date
                true
                (fun fields -> fields.Payment |> Option.map _.PaymentDate)
            row
                BusinessField.Status
                "status"
                FieldGroup.Status
                ScalarKind.Status
                false
                (fun fields -> Some(CaseStatuses.token fields.Status))
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
