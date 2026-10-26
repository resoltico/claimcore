namespace ClaimCore.Domain

open ClaimCore.Contract

/// A scalar description of an existing business field.
type FieldDefinition =
    {
        Field: BusinessField
        Scalar: ScalarRule
    }

[<RequireQualifiedAccess>]
type TextInput =
    | CaseReference
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | Reason

[<RequireQualifiedAccess>]
type DateInput =
    | IncidentDate
    | IncidentNotificationDate
    | PaymentDecisionDate
    | PaymentDate

[<RequireQualifiedAccess>]
type AmountInput =
    | ClaimedAmount
    | PayableAmount

module ScalarInputs =
    let textTarget =
        function
        | TextInput.CaseReference -> InputTarget.Field BusinessField.CaseReference
        | TextInput.IncidentCountry -> InputTarget.Field BusinessField.IncidentCountry
        | TextInput.ClaimantName -> InputTarget.Field BusinessField.ClaimantName
        | TextInput.InsurerName -> InputTarget.Field BusinessField.InsurerName
        | TextInput.Reason -> InputTarget.Reason

    let dateTarget =
        function
        | DateInput.IncidentDate -> InputTarget.Field BusinessField.IncidentDate
        | DateInput.IncidentNotificationDate ->
            InputTarget.Field BusinessField.IncidentNotificationDate
        | DateInput.PaymentDecisionDate -> InputTarget.Field BusinessField.PaymentDecisionDate
        | DateInput.PaymentDate -> InputTarget.Field BusinessField.PaymentDate

    let amountTargets =
        function
        | AmountInput.ClaimedAmount ->
            InputTarget.Field BusinessField.ClaimedAmount,
            InputTarget.Field BusinessField.ClaimedCurrency
        | AmountInput.PayableAmount ->
            InputTarget.Field BusinessField.PayableAmount,
            InputTarget.Field BusinessField.PayableCurrency

module FieldDefinitions =
    let private token grammar : ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = Some grammar
                }
            MaximumCharacters = None
        }

    let private freeText: ScalarTextConstraints =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = None
                }
            MaximumCharacters = Some TextAdmission.MaximumCharacters
        }

    let text =
        function
        | TextInput.CaseReference -> token Formats.reference
        | TextInput.IncidentCountry -> token Formats.country
        | TextInput.ClaimantName
        | TextInput.InsurerName
        | TextInput.Reason -> freeText

    let calendarDate: CalendarDateScalarRule =
        {
            ExactFormat = Formats.CalendarDateFormat
            Minimum = System.DateOnly.MinValue
            Maximum = System.DateOnly.MaxValue
        }

    let amount: AmountScalarRule =
        {
            Text = token Formats.amount
            Grammar = Formats.amount
            MaximumIntegerDigits = Formats.AmountIntegerDigits
            MaximumFractionalDigits = Formats.AmountFractionalDigits
        }

    let currency: CurrencyScalarRule =
        {
            Text = token Formats.currency
            Grammar = Formats.currency
            ExactCharacters = Formats.CurrencyCharacters
        }

    let private forScalar =
        function
        | ScalarKind.Date -> ScalarRule.CalendarDate calendarDate
        | ScalarKind.Country -> ScalarRule.Text(text TextInput.IncidentCountry)
        | ScalarKind.Reference -> ScalarRule.Text(text TextInput.CaseReference)
        | ScalarKind.Text -> ScalarRule.Text freeText
        | ScalarKind.Amount -> ScalarRule.Amount amount
        | ScalarKind.Currency -> ScalarRule.Currency currency
        | ScalarKind.Status -> ScalarRule.CaseStatus { AllowedValues = CaseStatuses.all }

    let all =
        FieldTable.rows
        |> List.map (fun row ->
            {
                Field = row.Field
                Scalar = forScalar row.Kind
            })

    let forField field =
        all |> List.find (fun definition -> definition.Field = field)

    /// Field order comes from the authoritative table; typed readers belong to the same table.
    let values fields =
        FieldTable.rows |> List.map (fun row -> row.Field, row.AcceptedValue fields)
