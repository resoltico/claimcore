module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open Expecto
open ClaimCore.Protocol
open ClaimCore.Json

let private accepted result =
    result
    |> Result.defaultWith (fun _ -> failtest "The fixture requires a permanently valid value.")

/// Test input adapters preserve authored domain groups while selecting a reference explicitly.
let caseRequest (input: UnvalidatedCaseRequest) : CaseOperationRequest =
    {
        OperationId = input.OperationId
        Target = CaseSelector.Reference input.CaseReference
        ExpectedRevision = input.ExpectedRevision
        Act = input.Act
    }

let caseOperation input =
    caseRequest input |> OperationInput.Case

let referencedCase (input: CaseOperationRequest) : UnvalidatedCaseRequest =
    match input.Target with
    | CaseSelector.Reference reference ->
        {
            OperationId = input.OperationId
            CaseReference = reference
            ExpectedRevision = input.ExpectedRevision
            Act = input.Act
        }
    | CaseSelector.Id _ ->
        failtest "This reference-only test stub requires an explicit reference fixture."

let workBasis: WorkDateBasis =
    {
        ObservedAt = System.DateTimeOffset(2026, 8, 31, 12, 0, 0, System.TimeSpan.Zero)
        BusinessDate = System.DateOnly(2026, 8, 31)
        TimeZoneId = "UTC"
    }

let unscheduledWork: CaseWorkView =
    {
        Responsible = None
        FollowUp = None
        Due = CaseDue.Unscheduled
        Basis = workBasis
    }

/// Presentation fixture for an unassigned case. Tests of relation disclosure supply explicit accounts.
let workView (state: CaseState<CaseFields>) : CaseWorkView =
    let fields = CaseState.fields state
    let work = fields |> Option.map _.Work |> Option.defaultValue CaseWork.empty

    if work.Responsible.IsSome then
        failtest "An assigned case needs an explicit account presentation fixture."

    {
        Responsible = None
        FollowUp = work.FollowUp
        Due =
            CaseDue.at
                workBasis.BusinessDate
                (CaseState.lifecycle state)
                (fields |> Option.map _.Status)
                work
        Basis = workBasis
    }

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
    RegistrationInput.replace values registration |> accepted

let registrationWith changes value =
    RegistrationInput.replace changes value |> accepted

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

let decisionFrom values =
    DecisionInput.replace values decision |> accepted

let decisionWith changes value =
    DecisionInput.replace changes value |> accepted

let paymentFrom values =
    PaymentInput.replace values payment |> accepted

let paymentWith changes value =
    PaymentInput.replace changes value |> accepted

let caseListPage page =
    {
        CaseListRequest.Page = page
        Filter = CaseListFilter.empty<string>
    }

let correction: AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = Change.Keep
        Payment = PaymentChange.Keep
    }

/// Build a request asserting all registration values; this is not a compatibility reader or an oracle.
let registrationUpdate (input: RegistrationInput) : RegistrationUpdateInput =
    let value field = RegistrationInput.value field input

    {
        IncidentDate = Some(value BusinessField.IncidentDate)
        NotificationDate = Some(value BusinessField.IncidentNotificationDate)
        Country = Some(value BusinessField.IncidentCountry)
        Claimant = Some(value BusinessField.ClaimantName)
        Insurer = Some(value BusinessField.InsurerName)
        Claimed =
            Some
                {
                    Amount = value BusinessField.ClaimedAmount
                    Currency = value BusinessField.ClaimedCurrency
                }
    }

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

let registrationChoice =
    function
    | Amendment.Keep -> Amendment.Keep
    | Amendment.Update value -> Amendment.Update(registrationUpdate value)

let decisionChoice =
    function
    | Change.Keep -> Change.Keep
    | Change.Clear -> Change.Clear
    | Change.Update value -> Change.Update(decisionUpdate value)

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
                        "kind", JsonValue.String "UPDATE"
                        "values",
                        RequestCodecs.registrationUpdate.Write(registrationUpdate registration)
                    ]
                "decision",
                JsonValue.Object
                    [
                        "kind", JsonValue.String "UPDATE"
                        "values", RequestCodecs.decisionUpdate.Write(decisionUpdate decision)
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
            | "REGISTRATION_UPDATE" ->
                [
                    "values",
                    RequestCodecs.registrationUpdate.Write(registrationUpdate registration)
                ]
            | "WORK" ->
                [
                    "values",
                    RequestCodecs.work.Write
                        {
                            CaseWorkInput.Responsible = WorkValue.Keep
                            FollowUp = WorkValue.Set "2026-09-01"
                        }
                ]
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

        match kind with
        | AuthorityKind.ProvisionOwner -> [ AuthorityAct.ProvisionOwner, token ]
        | AuthorityKind.Register -> [ AuthorityAct.Register, token ]
        | AuthorityKind.Enable -> [ AuthorityAct.Enable, token ]
        | AuthorityKind.Disable -> [ AuthorityAct.Disable, token ]
        | AuthorityKind.Grant -> grants |> List.map (fun grant -> AuthorityAct.Grant grant, token)
        | AuthorityKind.Revoke ->
            grants |> List.map (fun grant -> AuthorityAct.Revoke grant, token))

// Positive JSON seeds are authored overrides, not expectations. Missing fields come from the
// same input builders; unknown members and supplied values are never discarded or normalized.
let private groupBaselines =
    Map.ofList
        [
            "registration", RequestCodecs.registration.Write registration
            "decision", RequestCodecs.decision.Write decision
            "payment", RequestCodecs.payment.Write payment
        ]

let private groupOverride group members =
    match groupBaselines[group] with
    | JsonValue.Object baseline ->
        let supplied = Map.ofList members
        let keys = baseline |> List.map fst |> Set.ofList

        JsonValue.Object(
            (baseline
             |> List.map (fun (key, value) ->
                 key, Map.tryFind key supplied |> Option.defaultValue value))
            @ (members |> List.filter (fun (key, _) -> not (Set.contains key keys)))
        )
    | _ -> failtest "A group builder must produce an object."

let private payloadGroup group members =
    match members |> List.tryFind (fst >> (=) "kind") |> Option.map snd with
    | Some(JsonValue.String "REPLACE") -> group
    | Some(JsonValue.String token) ->
        Inventory.rows "caseActs"
        |> List.tryFind (fun row -> row.GetProperty("token").GetString() = token)
        |> Option.map (fun row ->
            row.GetProperty("payload").GetString() |> nonNull |> _.ToLowerInvariant())
        |> Option.filter (fun group -> Map.containsKey group groupBaselines)
    | _ -> None

let positiveValue value =
    let rec complete group =
        function
        | JsonValue.Object members ->
            let payload = payloadGroup group members

            let fields =
                members
                |> List.map (fun (key, value) ->
                    let child =
                        if key = "values" then payload
                        elif Map.containsKey key groupBaselines then Some key
                        else None

                    key, complete child value)

            match group with
            | Some group when not (members |> List.exists (fst >> (=) "kind")) ->
                groupOverride group fields
            | _ -> JsonValue.Object fields
        | JsonValue.Array values -> JsonValue.Array(List.map (complete None) values)
        | value -> value

    complete None value
