namespace ClaimCore.Contract

open ClaimCore.Domain

/// Presentation maps validated values to client fields; Domain owns every field and scalar rule.
module RecordFields =
    let vocabulary =
        Vocabulary.define "field" (Record.fields |> List.map _.Field) Record.key

    let labelId field =
        Vocabulary.messageKey vocabulary field "label"

    let meaningId field =
        Vocabulary.messageKey vocabulary field "meaning"

    let descriptor field =
        match Record.valueKind field with
        | FieldValueKind.CalendarDate -> ScalarRule.CalendarDate CalendarDate.descriptor
        | FieldValueKind.Country -> ScalarRule.Text CountryCode.descriptor
        | FieldValueKind.PartyName -> ScalarRule.Text PartyName.descriptor
        | FieldValueKind.Amount -> ScalarRule.Amount Money.descriptor.Amount
        | FieldValueKind.Currency -> ScalarRule.Currency Money.descriptor.Currency
        | FieldValueKind.Reference -> ScalarRule.Text CaseReference.descriptor
        | FieldValueKind.Status -> ScalarRule.CaseStatus { AllowedValues = CaseStatuses.all }

    // This exhaustive field projection formats data; it selects no business policy.
    // fsharplint:disable-next-line CyclomaticComplexity
    let value (fields: CaseFields) =
        function
        | BusinessField.CaseReference -> Some(CaseReference.format fields.CaseReference)
        | BusinessField.Status -> Some(CaseStatuses.token fields.Status)
        | BusinessField.IncidentDate -> Some(CalendarDate.format fields.Registration.IncidentDate)
        | BusinessField.IncidentNotificationDate ->
            Some(CalendarDate.format fields.Registration.IncidentNotificationDate)
        | BusinessField.IncidentCountry ->
            Some(CountryCode.format fields.Registration.IncidentCountry)
        | BusinessField.ClaimantName -> Some(PartyName.format fields.Registration.ClaimantName)
        | BusinessField.InsurerName -> Some(PartyName.format fields.Registration.InsurerName)
        | BusinessField.ClaimedAmount -> Some(Money.format fields.Registration.ClaimedAmount)
        | BusinessField.ClaimedCurrency -> Some(Money.currency fields.Registration.ClaimedAmount)
        | BusinessField.PaymentDecisionDate ->
            fields.Decision
            |> Option.map (fun value -> CalendarDate.format value.PaymentDecisionDate)
        | BusinessField.PayableAmount ->
            fields.Decision |> Option.map (fun value -> Money.format value.PayableAmount)
        | BusinessField.PayableCurrency ->
            fields.Decision |> Option.map (fun value -> Money.currency value.PayableAmount)
        | BusinessField.PaymentDate ->
            fields.Payment
            |> Option.map (fun value -> CalendarDate.format value.PaymentDate)

    let values fields =
        Record.fields
        |> List.map (fun definition -> definition.Field, value fields definition.Field)
