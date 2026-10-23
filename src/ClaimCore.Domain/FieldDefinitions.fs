namespace ClaimCore.Domain

open ClaimCore.Contract

/// Describes an existing business field; descriptors are not additional claim entries.
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
                ExactFormat = "yyyy-MM-dd"
                Minimum = System.DateOnly.MinValue
                Maximum = System.DateOnly.MaxValue
            }

    let private amount =
        let maximumIntegerDigits = 18
        let maximumFractionalDigits = 4

        let integerTail =
            (maximumIntegerDigits - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)

        let fractional =
            maximumFractionalDigits.ToString(System.Globalization.CultureInfo.InvariantCulture)

        let grammar =
            "(0|[1-9][0-9]{0," + integerTail + "})(\\.[0-9]{1," + fractional + "})?"

        ScalarRule.Amount
            {
                Text = token grammar
                Grammar = grammar
                MaximumIntegerDigits = maximumIntegerDigits
                MaximumFractionalDigits = maximumFractionalDigits
            }

    let private currency =
        let exactCharacters = 3

        let characters =
            exactCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)

        let grammar = "[A-Z]{" + characters + "}"

        ScalarRule.Currency
            {
                Text = token grammar
                Grammar = grammar
                ExactCharacters = exactCharacters
            }

    let private status = ScalarRule.CaseStatus { AllowedValues = CaseStatuses.all }

    let private forScalar field =
        match field with
        | BusinessField.IncidentDate
        | BusinessField.IncidentNotificationDate
        | BusinessField.PaymentDecisionDate
        | BusinessField.PaymentDate -> calendarDate
        | BusinessField.IncidentCountry -> ScalarRule.Text(token "[A-Z]{2}")
        | BusinessField.CaseReference -> ScalarRule.Text(token "[!-~]{1,80}")
        | BusinessField.ClaimantName
        | BusinessField.InsurerName -> ScalarRule.Text(text TextAdmission.MaximumCharacters)
        | BusinessField.ClaimedAmount
        | BusinessField.PayableAmount -> amount
        | BusinessField.ClaimedCurrency
        | BusinessField.PayableCurrency -> currency
        | BusinessField.Status -> status

    let all =
        FieldTable.rows
        |> List.map (fun row ->
            {
                Field = row.Field
                Scalar = forScalar row.Field
            })

    let forField field =
        all |> List.find (fun definition -> definition.Field = field)

    let scalar =
        function
        | InputField.Reason -> ScalarRule.Text(text TextAdmission.MaximumCharacters)
        | InputField.Business field -> (forField field).Scalar

    /// Exact values follow the authoritative field-table order.
    let values (fields: CaseFields) =
        let value =
            function
            | BusinessField.IncidentDate -> Some fields.IncidentDate
            | BusinessField.IncidentNotificationDate -> Some fields.IncidentNotificationDate
            | BusinessField.IncidentCountry -> Some fields.IncidentCountry
            | BusinessField.ClaimantName -> Some fields.ClaimantName
            | BusinessField.InsurerName -> Some fields.InsurerName
            | BusinessField.ClaimedAmount -> Some fields.ClaimedAmount
            | BusinessField.ClaimedCurrency -> Some fields.ClaimedCurrency
            | BusinessField.CaseReference -> Some fields.CaseReference
            | BusinessField.PaymentDecisionDate -> fields.PaymentDecisionDate
            | BusinessField.PayableAmount -> fields.PayableAmount
            | BusinessField.PayableCurrency -> fields.PayableCurrency
            | BusinessField.PaymentDate -> fields.PaymentDate
            | BusinessField.Status -> Some(CaseStatuses.token fields.Status)

        FieldTable.rows |> List.map (fun row -> row.Field, value row.Field)
