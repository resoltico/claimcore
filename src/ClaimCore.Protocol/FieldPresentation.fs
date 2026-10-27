namespace ClaimCore.Protocol

open ClaimCore.Domain

/// Presentation maps validated values to client fields; Domain owns every field and scalar rule.
module FieldPresentation =
    let labelId field =
        Vocabulary.messageKey BusinessField.vocabulary field "label"

    let meaningId field =
        Vocabulary.messageKey BusinessField.vocabulary field "meaning"

    let descriptor field =
        match BusinessField.valueKind field with
        | FieldValueKind.CalendarDate -> ScalarRule.CalendarDate CalendarDate.descriptor
        | FieldValueKind.Country -> ScalarRule.Text CountryCode.descriptor
        | FieldValueKind.PartyName -> ScalarRule.Text PartyName.descriptor
        | FieldValueKind.Amount -> ScalarRule.Amount Money.descriptor.Amount
        | FieldValueKind.Currency -> ScalarRule.Currency Money.descriptor.Currency
        | FieldValueKind.Reference -> ScalarRule.Text CaseReference.descriptor
        | FieldValueKind.Status -> ScalarRule.CaseStatus { AllowedValues = CaseStatus.all }
