module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.Application
open Expecto
open ClaimCore.Protocol
open ClaimCore.Json

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

/// Builders construct inputs only. Expected outputs and oracle values must stay literal.
let private groupInput (codec: ClaimCore.Json.Codec<'value>) group overrides =
    let samples =
        Map.ofList
            [
                "DATE", "2026-08-01"
                "COUNTRY", "LV"
                "NAME", "Synthetic Party"
                "AMOUNT", "1000.00"
                "CURRENCY", "EUR"
            ]

    Inventory.fields group
    |> List.map (fun field ->
        let key = field.GetProperty("key").GetString() |> nonNull
        let kind = field.GetProperty("kind").GetString() |> nonNull

        key,
        ClaimCore.Json.JsonValue.String(
            Map.tryFind key overrides |> Option.defaultValue samples[kind]
        ))
    |> ClaimCore.Json.JsonValue.Object
    |> codec.Read "$"
    |> Result.defaultWith (fun problem -> failtestf "Input inventory does not admit: %A" problem)

let registration: RegistrationInput =
    groupInput
        ClaimCore.Protocol.RequestCodecs.registration
        "registration"
        (Map.ofList [ "incidentNotificationDate", "2026-08-03"; "insurerName", "Synthetic Insurer" ])

let registrationFrom values =
    RegistrationInput.create values |> accepted

let registrationWith changes value =
    RegistrationInput.replace changes value |> accepted

let decisionFrom values = DecisionInput.create values |> accepted

let decisionWith changes value =
    DecisionInput.replace changes value |> accepted

let paymentFrom values = PaymentInput.create values |> accepted

let paymentWith changes value =
    PaymentInput.replace changes value |> accepted

let exampleRegistration =
    registrationWith
        [
            BusinessField.ClaimantName, "Example Claimant Ltd"
            BusinessField.InsurerName, "Example Alleged Insurer"
        ]
        registration

let registrationOn incident notification =
    registrationWith
        [
            BusinessField.IncidentDate, incident
            BusinessField.IncidentNotificationDate, notification
        ]
        registration

let decision: DecisionInput =
    groupInput
        ClaimCore.Protocol.RequestCodecs.decision
        "decision"
        (Map.ofList [ "paymentDecisionDate", "2026-08-15"; "payableAmount", "750.00" ])

let payment: PaymentInput =
    groupInput
        ClaimCore.Protocol.RequestCodecs.payment
        "payment"
        (Map.ofList [ "paymentDate", "2026-08-20" ])

let correction: AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = Change.Keep
        Payment = Change.Keep
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

let registrationStateFrom values = Registration.create values |> accepted

let registrationStateWith changes value =
    Registration.replace changes value |> accepted

let decisionStateFrom values = Decision.create values |> accepted

let decisionStateWith changes value =
    Decision.replace changes value |> accepted

let paymentStateFrom values = Payment.create values |> accepted

let authorityRequest operationId target act : ClaimCore.Domain.AuthorityRequest =
    AuthorityRequests.input operationId target act
    |> Result.defaultWith (fun _ -> failtest "Owner provisioning is not an authored test input.")

/// Inventory supplies the alternatives and expected tokens; codecs construct inputs only.
let caseActs registration decision payment reason =
    let groups =
        JsonValue.Object
            [
                "registration",
                JsonValue.Object
                    [
                        "kind", JsonValue.String "REPLACE"
                        "values", RequestCodecs.registration.Write registration
                    ]
                "decision",
                JsonValue.Object
                    [
                        "kind", JsonValue.String "REPLACE"
                        "values", RequestCodecs.decision.Write decision
                    ]
                "payment",
                JsonValue.Object
                    [
                        "kind", JsonValue.String "REPLACE"
                        "values", RequestCodecs.payment.Write payment
                    ]
            ]

    Inventory.rows "caseActs"
    |> List.map (fun row ->
        let token = row.GetProperty("token").GetString() |> nonNull
        let payload = row.GetProperty("payload").GetString() |> nonNull

        let members =
            match payload with
            | "BARE" -> []
            | "REGISTRATION" -> [ "values", RequestCodecs.registration.Write registration ]
            | "DECISION" -> [ "values", RequestCodecs.decision.Write decision ]
            | "PAYMENT" -> [ "values", RequestCodecs.payment.Write payment ]
            | "REASON" -> [ "reason", JsonValue.String reason ]
            | "CORRECTION" -> [ "groups", groups ]
            | _ -> failtestf "Unknown authored payload: %s" payload

        let act =
            JsonValue.Object(("kind", JsonValue.String token) :: members)
            |> RequestCodecs.caseAct.Read "$"
            |> Result.defaultWith (fun problem -> failtestf "Inventory act refused: %A" problem)

        act, token)

let authorityActs grants =
    Inventory.tokens "authorityActs"
    |> List.collect (fun token ->
        let kind = AuthorityKind.parse token |> Option.get

        match AuthorityKind.describe kind with
        | AuthorityActSlot.Bare act -> [ act, token ]
        | AuthorityActSlot.Grant(inject, _) ->
            grants |> List.map (fun grant -> inject grant, token))
