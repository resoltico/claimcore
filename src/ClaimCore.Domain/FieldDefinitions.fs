namespace ClaimCore.Domain

/// Describes an existing business field; descriptors are not additional claim entries.
type FieldDefinition =
    {
        Name: BusinessField
        Label: string
        Meaning: string
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
            Label = label
            Meaning = meaning
            Scalar = scalar
            AllowsAbsence = allowsAbsence
        }

    let private incidentFields =
        [
            field
                BusinessField.IncidentDate
                "Incident date"
                "Date on which the incident occurred."
                calendarDate
                false
            field
                BusinessField.IncidentNotificationDate
                "Incident notification date (FNOL)"
                "Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified."
                calendarDate
                false
            field
                BusinessField.IncidentCountry
                "Country of incident (ISO alpha-2 code)"
                "Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international."
                (ScalarRule.Text(token "[A-Z]{2}"))
                false
        ]

    let private registrationPartiesAndClaim =
        [
            field
                BusinessField.ClaimantName
                "Claimant name"
                "Name of the person or company making the claim."
                (ScalarRule.Text(text TextAdmission.MaximumCharacters))
                false
            field
                BusinessField.InsurerName
                "Responsible insurer"
                "Insurer recorded by the claims handler as responsible for this claim."
                (ScalarRule.Text(text TextAdmission.MaximumCharacters))
                false
            field
                BusinessField.ClaimedAmount
                "Amount claimed"
                "Amount requested by the claimant."
                amount
                false
            field
                BusinessField.ClaimedCurrency
                "Currency of claimed amount"
                "Currency in which the amount is claimed."
                currency
                false
        ]

    let private caseIdentity =
        [
            field
                BusinessField.CaseReference
                "Handler's case reference"
                "Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained."
                (ScalarRule.Text(token "[!-~]{1,80}"))
                false
        ]

    let private decisionPaymentAndStatus =
        [
            field
                BusinessField.PaymentDecisionDate
                "Payment decision date"
                "Date when this claims handler decided the amount to be paid. Not recorded until a decision is made."
                calendarDate
                true
            field
                BusinessField.PayableAmount
                "Amount to be paid"
                "Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount."
                amount
                true
            field
                BusinessField.PayableCurrency
                "Currency of amount to be paid"
                "Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency."
                currency
                true
            field
                BusinessField.PaymentDate
                "Payment date"
                "Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer."
                calendarDate
                true
            field
                BusinessField.Status
                "Case status"
                "Whether the case is open or closed. Closing a case does not mean that payment was made."
                status
                false
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
