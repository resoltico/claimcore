namespace ClaimCore.Domain

open ClaimCore.Catalog

/// Describes an existing business field; descriptors are not additional claim entries.
type FieldDefinition =
    {
        Name: BusinessField
        LabelId: string
        MeaningId: string
        Scalar: ScalarRule
        AllowsAbsence: bool
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

    let private field name label meaning scalar allowsAbsence =
        {
            Name = name
            LabelId = label
            MeaningId = meaning
            Scalar = scalar
            AllowsAbsence = allowsAbsence
        }

    let private incidentFields =
        [
            field
                BusinessField.IncidentDate
                "field.incidentDate.label"
                "field.incidentDate.meaning"
                calendarDate
                false
            field
                BusinessField.IncidentNotificationDate
                "field.incidentNotificationDate.label"
                "field.incidentNotificationDate.meaning"
                calendarDate
                false
            field
                BusinessField.IncidentCountry
                "field.incidentCountry.label"
                "field.incidentCountry.meaning"
                (ScalarRule.Text(token "[A-Z]{2}"))
                false
        ]

    let private registrationPartiesAndClaim =
        [
            field
                BusinessField.ClaimantName
                "field.claimantName.label"
                "field.claimantName.meaning"
                (ScalarRule.Text(text TextAdmission.MaximumCharacters))
                false
            field
                BusinessField.InsurerName
                "field.insurerName.label"
                "field.insurerName.meaning"
                (ScalarRule.Text(text TextAdmission.MaximumCharacters))
                false
            field
                BusinessField.ClaimedAmount
                "field.claimedAmount.label"
                "field.claimedAmount.meaning"
                amount
                false
            field
                BusinessField.ClaimedCurrency
                "field.claimedCurrency.label"
                "field.claimedCurrency.meaning"
                currency
                false
        ]

    let private caseIdentity =
        [
            field
                BusinessField.CaseReference
                "field.caseReference.label"
                "field.caseReference.meaning"
                (ScalarRule.Text(token "[!-~]{1,80}"))
                false
        ]

    let private decisionPaymentAndStatus =
        [
            field
                BusinessField.PaymentDecisionDate
                "field.paymentDecisionDate.label"
                "field.paymentDecisionDate.meaning"
                calendarDate
                true
            field
                BusinessField.PayableAmount
                "field.payableAmount.label"
                "field.payableAmount.meaning"
                amount
                true
            field
                BusinessField.PayableCurrency
                "field.payableCurrency.label"
                "field.payableCurrency.meaning"
                currency
                true
            field
                BusinessField.PaymentDate
                "field.paymentDate.label"
                "field.paymentDate.meaning"
                calendarDate
                true
            field BusinessField.Status "field.status.label" "field.status.meaning" status false
        ]

    let all =
        incidentFields
        @ registrationPartiesAndClaim
        @ caseIdentity
        @ decisionPaymentAndStatus

    let forField field =
        all |> List.find (fun definition -> definition.Name = field)

    let scalar =
        function
        | InputField.Reason -> ScalarRule.Text(text TextAdmission.MaximumCharacters)
        | InputField.Business field -> (forField field).Scalar

    /// Core-owned scalar projection in the same order as the thirteen-field definition.
    /// Values stay exact text; adapters choose layout, not interpretation or calculations.
    let values (fields: CaseFields) =
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
