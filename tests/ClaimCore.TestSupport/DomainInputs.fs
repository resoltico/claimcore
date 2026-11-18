module ClaimCore.TestSupport.DomainInputs

open ClaimCore.Domain
open Expecto

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let workBasis: WorkDateBasis =
    {
        ObservedAt = System.DateTimeOffset(2026, 8, 31, 12, 0, 0, System.TimeSpan.Zero)
        BusinessDate = System.DateOnly(2026, 8, 31)
        TimeZoneId = "UTC"
    }


/// A complete stage value for transport-only fixtures; behavior tests use literal expected stages.
let stageDisplay state =
    CaseStages.describe
        state
        (Some(CalendarDate.ofDate workBasis.BusinessDate))
        (Some(CalendarDate.ofDate workBasis.BusinessDate))
        None
    |> Result.defaultWith invalidOp


/// Domain metadata supplies authored input membership and scalar samples.
let fields group =
    let group =
        match group with
        | "registration" -> RecordGroup.Registration
        | "decision" -> RecordGroup.Decision
        | "payment" -> RecordGroup.Payment
        | _ -> invalidArg (nameof group) "Unknown input group."

    CaseFields.ofGroup group
    |> List.map (fun field ->
        let declaration = BusinessField.describe field in declaration, declaration.AllowsAbsence)

let fieldKeys group =
    fields group |> List.map (fun (field, _) -> field.Key)

/// Builders construct inputs only. Expected outputs and oracle values must stay literal.
let private groupInput create group overrides =
    let samples =
        Map.ofList
            [
                FieldValueKind.CalendarDate, "2026-08-01"
                FieldValueKind.Country, "LV"
                FieldValueKind.Identifier, "SYN-1"
                FieldValueKind.PartyName, "Synthetic Party"
                FieldValueKind.IncidentDescription, "Synthetic incident description"
                FieldValueKind.Amount, "1000.00"
                FieldValueKind.Currency, "EUR"
            ]

    fields group
    |> List.choose (fun (field, optional) ->
        let key = field.Key
        let kind = field.Kind

        if optional && not (Map.containsKey key overrides) then
            None
        else
            Some(field.Field, Map.tryFind key overrides |> Option.defaultValue samples[kind]))
    |> create
    |> accepted

let registration: RegistrationInput =
    groupInput
        RegistrationInput.create
        "registration"
        (Map.ofList
            [
                "incidentDate", "2026-08-01"
                "receivedDate", "2026-08-03"
                "incidentCountry", "LV"
                "policyNumber", "Synthetic Policy"
                "insurerClaimReference", "Synthetic Insurer Reference"
                "claimedAmount", "1000.00"
                "claimedCurrency", "EUR"
            ])

let registrationFrom values =
    RegistrationInput.replace values registration |> accepted

let registrationWith changes value =
    RegistrationInput.replace changes value |> accepted

let exampleRegistration =
    registrationWith
        [
            BusinessField.PolicyNumber, "Example Claimant Ltd"
            BusinessField.InsurerClaimReference, "Example Alleged Insurer"
        ]
        registration

let registrationOn incident notification =
    registrationWith
        [
            BusinessField.IncidentDate, incident
            BusinessField.ReceivedDate, notification
        ]
        registration

let decision: DecisionInput =
    groupInput
        DecisionInput.create
        "decision"
        (Map.ofList [ "paymentDecisionDate", "2026-08-15"; "payableAmount", "750.00" ])

let payment: PaymentInput =
    groupInput
        PaymentInput.create
        "payment"
        (Map.ofList [ "paymentDate", "2026-08-20"; "paidAmount", "750"; "paidCurrency", "EUR" ])

let decisionFrom values =
    DecisionInput.replace values decision |> accepted

let decisionWith changes value =
    DecisionInput.replace changes value |> accepted

let paymentFrom values =
    PaymentInput.replace values payment |> accepted

let paymentWith changes value =
    PaymentInput.replace changes value |> accepted


/// Build a request asserting all registration values; this is not a compatibility reader or an oracle.
let registrationUpdate (input: RegistrationInput) : RegistrationUpdateInput =
    RegistrationInput.values input
    |> List.map (fun (field, value) -> field, Some value)
    |> RegistrationUpdateInput.create
    |> accepted

let decisionUpdate (input: DecisionInput) : DecisionUpdateInput =
    let value field = DecisionInput.value field input

    {
        Date = Some(value BusinessField.PaymentDecisionDate)
        Payable =
            Some
                {
                    Amount = value BusinessField.PayableAmount
                    Currency = value BusinessField.PayableCurrency
                }
    }

let principal: PrincipalInput =
    {
        Kind = PrincipalKind.Human
        Issuer = "https://issuer.example/"
        Subject = "synthetic-owner"
    }

let date value = CalendarDate.parse value |> accepted
let country value = CountryCode.parse value |> accepted
let name value = PartyName.parse value |> accepted
let reference value = CaseReference.parse value |> accepted

let money amount currency =
    Money.create (Amount.parse amount |> accepted) (Currency.parse currency |> accepted)

let private admitGroup values create =
    values
    |> List.map (fun (field, value) ->
        field, FieldValue.parse (BusinessField.describe field).Kind value |> accepted)
    |> create
    |> accepted

let admittedRegistration input =
    admitGroup (RegistrationInput.values input) Registration.create

let admittedDecision input =
    admitGroup (DecisionInput.values input) Decision.create

let admittedPayment input =
    admitGroup (PaymentInput.values input) Payment.create

let registrationStateFrom values =
    Registration.replace values (admittedRegistration registration) |> accepted

let registrationStateWith changes value =
    Registration.replace changes value |> accepted

let decisionStateFrom values =
    Decision.replace values (admittedDecision decision) |> accepted

let decisionStateWith changes value =
    Decision.replace changes value |> accepted

let paymentStateFrom values =
    Payment.replace values (admittedPayment payment) |> accepted

// Explicit authored fixture names; identity-provider claims never supply an actor profile.
let actorProfile: ActorProfileInput =
    {
        DisplayName = "Synthetic account"
        Contact = None
    }

let ownerProfile: ActorProfileInput =
    {
        DisplayName = "Synthetic owner"
        Contact = None
    }

let actorReference id =
    ActorRef.create id PrincipalKind.Human ActorAvailability.Available None

let namedActorReference id name =
    let profile =
        ActorProfile.parse { DisplayName = name; Contact = None }
        |> Result.defaultWith (fun _ -> failtest "Invalid authored fixture name")

    ActorRef.create id PrincipalKind.Human ActorAvailability.Available (Some profile)
