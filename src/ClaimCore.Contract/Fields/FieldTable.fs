namespace ClaimCore.Contract

[<NoEquality; NoComparison>]
type FieldRow =
    {
        Field: BusinessField
        Key: string
        Placement: FieldPlacement
        RetainedOnErasure: bool
        Scalar: ScalarRule
        GrammarViolation: Violation option
        Optional: bool
        AcceptedValue: CaseFields -> string option
    }

module FieldTable =
    let private row field key placement scalar violation optional read =
        {
            Field = field
            Key = key
            Placement = placement
            RetainedOnErasure = field = BusinessField.CaseReference
            Scalar = scalar
            GrammarViolation = violation
            Optional = optional
            AcceptedValue = read
        }

    let rows =
        [
            row
                BusinessField.IncidentDate
                "incidentDate"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.CalendarDate FieldRules.calendarDate)
                None
                false
                (fun fields -> Some fields.Registration.IncidentDate)
            row
                BusinessField.IncidentNotificationDate
                "incidentNotificationDate"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.CalendarDate FieldRules.calendarDate)
                None
                false
                (fun fields -> Some fields.Registration.IncidentNotificationDate)
            row
                BusinessField.IncidentCountry
                "incidentCountry"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.Text(FieldRules.token Formats.country))
                (Some Violation.CountryFormat)
                false
                (fun fields -> Some fields.Registration.IncidentCountry)
            row
                BusinessField.ClaimantName
                "claimantName"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.Text FieldRules.freeText)
                None
                false
                (fun fields -> Some fields.Registration.ClaimantName)
            row
                BusinessField.InsurerName
                "insurerName"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.Text FieldRules.freeText)
                None
                false
                (fun fields -> Some fields.Registration.InsurerName)
            row
                BusinessField.ClaimedAmount
                "claimedAmount"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.Amount FieldRules.amount)
                None
                false
                (fun fields -> Some fields.Registration.ClaimedAmount)
            row
                BusinessField.ClaimedCurrency
                "claimedCurrency"
                (FieldPlacement.Grouped RecordGroup.Registration)
                (ScalarRule.Currency FieldRules.currency)
                None
                false
                (fun fields -> Some fields.Registration.ClaimedCurrency)
            row
                BusinessField.CaseReference
                "caseReference"
                FieldPlacement.TopLevel
                (ScalarRule.Text(FieldRules.token Formats.reference))
                (Some Violation.ReferenceFormat)
                false
                (fun fields -> Some fields.CaseReference)
            row
                BusinessField.PaymentDecisionDate
                "paymentDecisionDate"
                (FieldPlacement.Grouped RecordGroup.Decision)
                (ScalarRule.CalendarDate FieldRules.calendarDate)
                None
                true
                (fun fields -> fields.Decision |> Option.map _.PaymentDecisionDate)
            row
                BusinessField.PayableAmount
                "payableAmount"
                (FieldPlacement.Grouped RecordGroup.Decision)
                (ScalarRule.Amount FieldRules.amount)
                None
                true
                (fun fields -> fields.Decision |> Option.map _.PayableAmount)
            row
                BusinessField.PayableCurrency
                "payableCurrency"
                (FieldPlacement.Grouped RecordGroup.Decision)
                (ScalarRule.Currency FieldRules.currency)
                None
                true
                (fun fields -> fields.Decision |> Option.map _.PayableCurrency)
            row
                BusinessField.PaymentDate
                "paymentDate"
                (FieldPlacement.Grouped RecordGroup.Payment)
                (ScalarRule.CalendarDate FieldRules.calendarDate)
                None
                true
                (fun fields -> fields.Payment |> Option.map _.PaymentDate)
            row
                BusinessField.Status
                "status"
                FieldPlacement.TopLevel
                (ScalarRule.CaseStatus { AllowedValues = CaseStatuses.all })
                None
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

    let values fields =
        rows |> List.map (fun row -> row.Field, row.AcceptedValue fields)

    let textRule field =
        match (forField field).Scalar with
        | ScalarRule.Text constraints -> constraints
        | _ -> invalidArg (nameof field) "Expected a text field."

    let dateRule field =
        match (forField field).Scalar with
        | ScalarRule.CalendarDate rule -> rule
        | _ -> invalidArg (nameof field) "Expected a calendar-date field."

    let amountRule field =
        match (forField field).Scalar with
        | ScalarRule.Amount rule -> rule
        | _ -> invalidArg (nameof field) "Expected an amount field."

    let currencyRule field =
        match (forField field).Scalar with
        | ScalarRule.Currency rule -> rule
        | _ -> invalidArg (nameof field) "Expected a currency field."
