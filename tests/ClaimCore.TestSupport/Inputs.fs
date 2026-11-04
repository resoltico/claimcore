module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open Expecto
open ClaimCore.Protocol
open ClaimCore.Json

let activeContext (reply: ContextReply) =
    match reply.Requester with
    | RequesterState.Active context -> context
    | other -> failtestf "Fixture requires ACTIVE requester, observed %A" other

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
        Revision = Revision.zero
        ChangedAt = None
        RecordedBy = None
        Summary = None
        NextAction = None
        PendingReminders = 0L
        OverdueReminders = 0L
        NeedsNextAction = true
        CanUpdateSummary = true
        CanAssign = true
        CanCreateReminder = true
        Responsible = None
        FollowUp = None
        Due = CaseDue.Unscheduled
        Basis = workBasis
    }

/// A fact snapshot has no authored work. Relation/work tests provide their independent work fixture.
let workView (state: CaseState<CaseFields>) : CaseWorkView =
    let fields = CaseState.fields state
    let active = CaseState.lifecycle state = Lifecycle.Active

    let actionable =
        active
        && (fields |> Option.exists (fun fields -> fields.Status = CaseStatus.Opened))

    { unscheduledWork with
        Due =
            CaseDue.at
                workBasis.BusinessDate
                (CaseState.lifecycle state)
                (fields |> Option.map _.Status)
                None
        NeedsNextAction = actionable
        CanUpdateSummary = active
        CanAssign = actionable
        CanCreateReminder = actionable
    }

/// A complete stage value for transport-only fixtures; behavior tests use literal expected stages.
let stageDisplay state =
    CaseStages.describe
        state
        (Some(CalendarDate.ofDate workBasis.BusinessDate))
        (Some(CalendarDate.ofDate workBasis.BusinessDate))
        None
    |> Result.defaultWith invalidOp

/// Builders construct inputs only. Expected outputs and oracle values must stay literal.
let private groupInput (codec: ClaimCore.Json.Codec<'value>) group overrides =
    let samples =
        Map.ofList
            [
                "DATE", "2026-08-01"
                "COUNTRY", "LV"
                "NAME", "Synthetic Party"
                "DESCRIPTION", "Synthetic incident description"
                "AMOUNT", "1000.00"
                "CURRENCY", "EUR"
            ]

    Inventory.fields group
    |> List.choose (fun field ->
        let key = field.GetProperty("key").GetString() |> nonNull
        let kind = field.GetProperty("kind").GetString() |> nonNull

        let optional =
            match field.TryGetProperty "optional" with
            | true, value -> value.GetBoolean()
            | _ -> false

        if optional && not (Map.containsKey key overrides) then
            None
        else
            Some(
                key,
                ClaimCore.Json.JsonValue.String(
                    Map.tryFind key overrides |> Option.defaultValue samples[kind]
                )
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
        IncidentDescription =
            RegistrationInput.description input
            |> Option.map WorkValue.Set
            |> Option.defaultValue WorkValue.Keep
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
    match act with
    | AuthorityAct.ProvisionOwner _ -> failtest "Owner provisioning is not an authored test input."
    | _ ->
        {
            OperationId = operationId
            Target = PrincipalKey.toInput target
            Act = act
        }

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

let authorityActs grants =
    Inventory.tokens "authorityActs"
    |> List.collect (fun token ->
        let kind = AuthorityKind.parse token |> Option.get

        match kind with
        | AuthorityKind.ProvisionOwner -> [ AuthorityAct.ProvisionOwner ownerProfile, token ]
        | AuthorityKind.Register -> [ AuthorityAct.Register actorProfile, token ]
        | AuthorityKind.Admit ->
            if List.isEmpty grants then
                []
            else
                [
                    AuthorityAct.Admit
                        {
                            Profile = actorProfile
                            Grants = List.truncate AuthorityAdmission.MaximumGrants grants
                        },
                    token
                ]
        | AuthorityKind.SetProfile -> [ AuthorityAct.SetProfile actorProfile, token ]
        | AuthorityKind.EraseProfile -> [ AuthorityAct.EraseProfile, token ]
        | AuthorityKind.Enable -> [ AuthorityAct.Enable, token ]
        | AuthorityKind.Disable -> [ AuthorityAct.Disable, token ]
        | AuthorityKind.Grant -> grants |> List.map (fun grant -> AuthorityAct.Grant grant, token)
        | AuthorityKind.Revoke ->
            grants |> List.map (fun grant -> AuthorityAct.Revoke grant, token))

/// Durable alternatives are independent of authored operation alternatives (ADMIT is not an effect).
let authorityEffects grants =
    let profile =
        ActorProfile.parse actorProfile
        |> Result.defaultWith (fun _ -> failtest "Profile fixture")

    [
        yield AuthorityEffect.Register principal, "REGISTER"
        yield AuthorityEffect.ProvisionOwner principal, "PROVISION_OWNER"
        yield AuthorityEffect.SetProfile profile, "SET_PROFILE"
        yield AuthorityEffect.EraseProfile, "ERASE_PROFILE"
        yield AuthorityEffect.Enable, "ENABLE"
        yield AuthorityEffect.Disable, "DISABLE"
        for grant in grants do
            yield AuthorityEffect.Grant grant, "GRANT"
            yield AuthorityEffect.Revoke grant, "REVOKE"
    ]

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

let private maintenanceDefaults fields =
    let keys = fields |> List.map fst |> Set.ofList
    let advice = Set.contains "acts" keys && Set.contains "history" keys
    let context = Set.contains "installationId" keys && Set.contains "capabilities" keys

    if (advice || context) && not (Set.contains "maintenance" keys) then
        fields
        @ [
            "maintenance",
            JsonValue.Object
                [ "active", JsonValue.Boolean false; "generation", JsonValue.Integer 0L ]
        ]
    else
        fields

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
            | _ -> JsonValue.Object(maintenanceDefaults fields)
        | JsonValue.Array values -> JsonValue.Array(List.map (complete None) values)
        | value -> value

    complete None value
