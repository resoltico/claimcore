namespace ClaimCore.Domain

open ClaimCore.Contract

/// Describes an existing business field; catalog are not additional claim entries.
type FieldDefinition =
    {
        Field: BusinessField
        Scalar: ScalarRule
    }

module FieldDefinitions =
    let private text maximumCharacters =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = None
                }
            Policy =
                Some
                    {
                        MaximumCharacters = maximumCharacters
                        RequiresNonBlank = true
                        RejectsSurroundingWhitespace = true
                        RejectsControlCharacters = true
                        RejectsForbiddenInvisibleCharacters = true
                    }
        }

    let private token grammar =
        {
            Format =
                {
                    MinimumCharacters = 1
                    Grammar = Some grammar
                }
            Policy = None
        }

    let private calendarDate =
        ScalarRule.CalendarDate
            {
                ExactFormat = Formats.CalendarDateFormat
                Minimum = System.DateOnly.MinValue
                Maximum = System.DateOnly.MaxValue
            }

    let private amount =
        ScalarRule.Amount
            {
                Text = token Formats.amount
                Grammar = Formats.amount
                MaximumIntegerDigits = Formats.AmountIntegerDigits
                MaximumFractionalDigits = Formats.AmountFractionalDigits
            }

    let private currency =
        ScalarRule.Currency
            {
                Text = token Formats.currency
                Grammar = Formats.currency
                ExactCharacters = Formats.CurrencyCharacters
            }

    let private status = ScalarRule.CaseStatus { AllowedValues = CaseStatuses.all }

    let private forScalar =
        function
        | ScalarKind.Date -> calendarDate
        | ScalarKind.Country -> ScalarRule.Text(token Formats.country)
        | ScalarKind.Reference -> ScalarRule.Text(token Formats.reference)
        | ScalarKind.Text -> ScalarRule.Text(text TextAdmission.MaximumCharacters)
        | ScalarKind.Amount -> amount
        | ScalarKind.Currency -> currency
        | ScalarKind.Status -> status

    let all =
        FieldTable.rows
        |> List.map (fun row ->
            {
                Field = row.Field
                Scalar = forScalar row.Kind
            })

    let forField field =
        all |> List.find (fun definition -> definition.Field = field)

    let scalar =
        function
        | InputTarget.Reason -> ScalarRule.Text(text TextAdmission.MaximumCharacters)
        | InputTarget.Field field -> (forField field).Scalar
        | _ -> invalidArg "field" "The target has no business scalar definition."

    /// Exact values follow the authoritative field-table order.
    let values (fields: CaseFields) =
        let values =
            [
                BusinessField.IncidentDate, Some fields.IncidentDate
                BusinessField.IncidentNotificationDate, Some fields.IncidentNotificationDate
                BusinessField.IncidentCountry, Some fields.IncidentCountry
                BusinessField.ClaimantName, Some fields.ClaimantName
                BusinessField.InsurerName, Some fields.InsurerName
                BusinessField.ClaimedAmount, Some fields.ClaimedAmount
                BusinessField.ClaimedCurrency, Some fields.ClaimedCurrency
                BusinessField.CaseReference, Some fields.CaseReference
                BusinessField.PaymentDecisionDate, fields.PaymentDecisionDate
                BusinessField.PayableAmount, fields.PayableAmount
                BusinessField.PayableCurrency, fields.PayableCurrency
                BusinessField.PaymentDate, fields.PaymentDate
                BusinessField.Status, Some(CaseStatuses.token fields.Status)
            ]
            |> Map.ofList

        FieldTable.rows |> List.map (fun row -> row.Field, Map.find row.Field values)
