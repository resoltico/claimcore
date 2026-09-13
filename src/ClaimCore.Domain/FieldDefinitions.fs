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
            MinimumCharacters = 1
            MaximumCharacters = maximumCharacters
            RequiresNonBlank = true
            RejectsSurroundingWhitespace = true
            RejectsControlCharacters = true
            RequiresWellFormedUnicode = true
        }

    let private calendarDate =
        ScalarRule.CalendarDate
            {
                ExactFormat = "yyyy-MM-dd"
                Minimum = System.DateOnly.MinValue
                Maximum = System.DateOnly.MaxValue
            }

    let private amount =
        ScalarRule.Amount
            {
                Text = text 23
                Grammar = "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?"
                MaximumIntegerDigits = 18
                MaximumFractionalDigits = 4
            }

    let private currency =
        ScalarRule.Currency
            {
                Text = text 3
                Grammar = "[A-Z]{3}"
                ExactCharacters = 3
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
                "Country of incident"
                "Country where the incident occurred; recorded as entered without inferring applicable law."
                (ScalarRule.Text(text 100))
                false
        ]

    let private registrationPartiesAndClaim =
        [
            field
                "claimantName"
                "ClaimantName"
                "Claimant name"
                "Name of the claimant, whether a private person or a company. No separate party-type or legal-identity fields."
                (ScalarRule.Text(text 200))
                false
            field
                "insurerName"
                "InsurerName"
                "Allegedly responsible insurer"
                "Name of the insurer claimed to be responsible. This does not establish ultimate liability or a recourse-chain position."
                (ScalarRule.Text(text 200))
                false
            field
                "claimedAmount"
                "ClaimedAmount"
                "Amount claimed"
                "Amount claimed by the claimant, encoded as exact non-negative decimal text. Zero is a recorded amount, not a substitute for unknown."
                amount
                false
            field
                "claimedCurrency"
                "ClaimedCurrency"
                "Currency of claimed amount"
                "Three-letter uppercase identifier for the claimed amount; no currency conversion or registry inference."
                currency
                false
        ]

    let private caseIdentity =
        [
            field
                "caseReference"
                "CaseReference"
                "Handler's case reference"
                "Reference assigned by the claims handler operating this register, not an insurer reference. Unique, immutable and case-sensitive within one installation."
                (ScalarRule.Text(text 80))
                false
        ]

    let private decisionPaymentAndStatus =
        [
            field
                "paymentDecisionDate"
                "PaymentDecisionDate"
                "Payment decision date"
                "Date when this claims handler decided the amount to be paid. Null until a decision is recorded; not a payment or bank instruction."
                calendarDate
                true
            field
                "payableAmount"
                "PayableAmount"
                "Amount to be paid"
                "Decided amount to be paid, in exact non-negative decimal text. Null until decided; may differ from the claimed amount."
                amount
                true
            field
                "payableCurrency"
                "PayableCurrency"
                "Currency of amount to be paid"
                "Currency of the decided amount. Null until decided; it need not equal the claimed currency."
                currency
                true
            field
                "paymentDate"
                "PaymentDate"
                "Payment date"
                "Date when the decided amount was actually paid, as recorded by the operator. Null while unpaid or unknown. One full payment only; recording does not execute or independently verify a bank transfer."
                calendarDate
                true
            field
                "status"
                "Status"
                "Case status"
                "Administrative state OPENED or CLOSED, independent of decision and payment. No additional case statuses."
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
    let scalar name = (forName name).Scalar

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
