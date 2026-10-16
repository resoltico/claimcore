namespace ClaimCore.Domain

/// Describes an existing business field; descriptors are not additional claim entries.
type FieldDefinition =
    {
        Name: string
        NativeName: string
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

    let private field name nativeName label meaning scalar allowsAbsence =
        {
            Name = name
            NativeName = nativeName
            Label = label
            Meaning = meaning
            Scalar = scalar
            AllowsAbsence = allowsAbsence
        }

    let private incidentFields =
        [
            field
                "incidentDate"
                "IncidentDate"
                "Incident date"
                "Date on which the incident occurred."
                calendarDate
                false
            field
                "incidentNotificationDate"
                "IncidentNotificationDate"
                "Incident notification date (FNOL)"
                "Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified."
                calendarDate
                false
            field
                "incidentCountry"
                "IncidentCountry"
                "Country of incident (ISO alpha-2 code)"
                "Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international."
                (ScalarRule.Text(token "[A-Z]{2}"))
                false
        ]

    let private registrationPartiesAndClaim =
        [
            field
                "claimantName"
                "ClaimantName"
                "Claimant name"
                "Name of the person or company making the claim."
                (ScalarRule.Text(text 200))
                false
            field
                "insurerName"
                "InsurerName"
                "Responsible insurer"
                "Insurer recorded by the claims handler as responsible for this claim."
                (ScalarRule.Text(text 200))
                false
            field
                "claimedAmount"
                "ClaimedAmount"
                "Amount claimed"
                "Amount requested by the claimant."
                amount
                false
            field
                "claimedCurrency"
                "ClaimedCurrency"
                "Currency of claimed amount"
                "Currency in which the amount is claimed."
                currency
                false
        ]

    let private caseIdentity =
        [
            field
                "caseReference"
                "CaseReference"
                "Handler's case reference"
                "Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained."
                (ScalarRule.Text(token "[!-~]{1,80}"))
                false
        ]

    let private decisionPaymentAndStatus =
        [
            field
                "paymentDecisionDate"
                "PaymentDecisionDate"
                "Payment decision date"
                "Date when this claims handler decided the amount to be paid. Not recorded until a decision is made."
                calendarDate
                true
            field
                "payableAmount"
                "PayableAmount"
                "Amount to be paid"
                "Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount."
                amount
                true
            field
                "payableCurrency"
                "PayableCurrency"
                "Currency of amount to be paid"
                "Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency."
                currency
                true
            field
                "paymentDate"
                "PaymentDate"
                "Payment date"
                "Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer."
                calendarDate
                true
            field
                "status"
                "Status"
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

    /// Unknown field keys are programmer errors; no user input chooses validation budgets.
    let forName name =
        all
        |> List.tryFind (fun field -> field.Name = name)
        |> Option.defaultWith (fun () -> invalidArg "name" "The field is not declared.")

    /// The scalar definition is the single source for Domain validation and semantic discovery.
    let scalar name =
        if name = "reason" then
            ScalarRule.Text(text 200)
        else
            (forName name).Scalar

    /// Core-owned scalar projection in the same order as the thirteen-field definition.
    /// Values stay exact text; adapters choose layout, not interpretation or calculations.
    let values (fields: CaseFields) =
        [
            "incidentDate", Some fields.IncidentDate
            "incidentNotificationDate", Some fields.IncidentNotificationDate
            "incidentCountry", Some fields.IncidentCountry
            "claimantName", Some fields.ClaimantName
            "insurerName", Some fields.InsurerName
            "claimedAmount", Some fields.ClaimedAmount
            "claimedCurrency", Some fields.ClaimedCurrency
            "caseReference", Some fields.CaseReference
            "paymentDecisionDate", fields.PaymentDecisionDate
            "payableAmount", fields.PayableAmount
            "payableCurrency", fields.PayableCurrency
            "paymentDate", fields.PaymentDate
            "status", Some(CaseStatuses.token fields.Status)
        ]
