namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Presentation maps validated values to client fields; Domain owns every field and scalar rule.
module FieldPresentation =
    let labelId field =
        Vocabulary.messageKey BusinessField.vocabulary field "label"

    let hintId field =
        Vocabulary.messageKey BusinessField.vocabulary field "hint"

    let ruleId field =
        match (BusinessField.describe field).Kind with
        | FieldValueKind.CalendarDate -> "scalar.calendarDate.guidance"
        | FieldValueKind.PartyName
        | FieldValueKind.Identifier -> "scalar.text.guidance"
        | FieldValueKind.HandlingSummary
        | FieldValueKind.IncidentDescription -> "scalar.narrative.guidance"
        | _ -> Vocabulary.messageKey BusinessField.vocabulary field "rule"

    let descriptor field =
        match (BusinessField.describe field).Kind with
        | FieldValueKind.PlannedDate -> ScalarRule.PlannedDate CalendarDate.descriptor
        | FieldValueKind.ActorId ->
            ScalarRule.Text
                {
                    Format =
                        {
                            MinimumCharacters = 36
                            Grammar =
                                Some
                                    "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
                            UnknownCode = None
                        }
                    MaximumCharacters = Some 36
                }
        | FieldValueKind.CalendarDate -> ScalarRule.CalendarDate CalendarDate.descriptor
        | FieldValueKind.HandlingSummary -> ScalarRule.PlainText HandlingSummary.constraints
        | FieldValueKind.IncidentDescription -> ScalarRule.PlainText IncidentDescription.constraints
        | FieldValueKind.Country -> ScalarRule.Text CountryCode.descriptor
        | FieldValueKind.PartyName -> ScalarRule.Text PartyName.descriptor
        | FieldValueKind.Identifier -> ScalarRule.Text Identifier.descriptor
        | FieldValueKind.Amount -> ScalarRule.Amount Money.descriptor.Amount
        | FieldValueKind.Currency -> ScalarRule.Currency Money.descriptor.Currency
        | FieldValueKind.Reference -> ScalarRule.Text CaseReference.descriptor
        | FieldValueKind.Status -> ScalarRule.CaseStatus { AllowedValues = CaseStatus.all }
