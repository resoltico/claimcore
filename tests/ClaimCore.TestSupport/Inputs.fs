module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.Application
open Expecto
open ClaimCore.Protocol
open ClaimCore.Json

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

let exampleRegistration =
    { registration with
        ClaimantName = "Example Claimant Ltd"
        InsurerName = "Example Alleged Insurer"
    }

let registrationOn incident notification =
    { registration with
        IncidentDate = incident
        IncidentNotificationDate = notification
    }

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

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

let date value = CalendarDate.parse value |> accepted
let country value = CountryCode.parse value |> accepted
let name value = PartyName.parse value |> accepted
let reference value = CaseReference.parse value |> accepted
let money amount currency = Money.parse amount currency |> accepted

let admittedRegistration (input: RegistrationInput) : Registration =
    {
        IncidentDate = date input.IncidentDate
        IncidentNotificationDate = date input.IncidentNotificationDate
        IncidentCountry = country input.IncidentCountry
        ClaimantName = name input.ClaimantName
        InsurerName = name input.InsurerName
        ClaimedAmount = money input.ClaimedAmount input.ClaimedCurrency
    }

let admittedDecision (input: DecisionInput) : Decision =
    {
        PaymentDecisionDate = date input.PaymentDecisionDate
        PayableAmount = money input.PayableAmount input.PayableCurrency
    }

let admittedPayment (input: PaymentInput) : Payment =
    { PaymentDate = date input.PaymentDate }


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

        match AuthorityAct.slot kind with
        | AuthorityActSlot.Bare act -> [ act, token ]
        | AuthorityActSlot.Grant(inject, _) ->
            grants |> List.map (fun grant -> inject grant, token))
