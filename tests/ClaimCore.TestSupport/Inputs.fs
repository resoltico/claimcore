module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
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
        SummaryChangedAt = None
        SummaryRecordedBy = None
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

/// A complete transport fixture; policy tests assert literal reasons from the actual service.
let caseAdvice state =
    let headline =
        state
        |> CaseState.map (fun (fields: CaseFields) ->
            fields.Status, CaseProgress.ofPayment fields.Progress)
        |> CaseHeadline.ofState

    let blocked =
        CaseActKind.all
        |> List.map (fun act ->
            {
                CaseActRefusal.Act = act
                Reason = Refusal.Case(CaseRefusal.NotOffered headline)
            })

    CaseAdvice.create [] None HistoryAccess.None blocked
    |> Result.defaultWith invalidOp

/// Wire codecs own authored input membership and ordering; Domain supplies scalar metadata.
let fields group =
    let schema, definitions =
        match group with
        | "registration" ->
            RequestCodecs.registration.Schema, RequestCodecs.registration.Definitions
        | "decision" -> RequestCodecs.decision.Schema, RequestCodecs.decision.Definitions
        | "payment" -> RequestCodecs.payment.Schema, RequestCodecs.payment.Definitions
        | _ -> invalidArg (nameof group) "Unknown record group."

    let rec keys =
        function
        | Schema.Ref name -> keys definitions[name]
        | Schema.Object(members, optional) ->
            members |> List.map (fun (key, _) -> key, List.contains key optional)
        | _ -> invalidOp "A group input must be an object."

    keys schema
    |> List.map (fun (key, optional) ->
        let field =
            Vocabulary.tryParse BusinessField.vocabulary key
            |> Option.get
            |> BusinessField.describe

        field, optional)

let fieldKeys group =
    fields group |> List.map (fun (field, _) -> field.Key)

/// Builders construct inputs only. Expected outputs and oracle values must stay literal.
let private groupInput (codec: ClaimCore.Json.Codec<'value>) group overrides =
    let samples =
        Map.ofList
            [
                FieldValueKind.CalendarDate, "2026-08-01"
                FieldValueKind.Country, "LV"
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
            Some(
                key,
                ClaimCore.Json.JsonValue.String(
                    Map.tryFind key overrides |> Option.defaultValue samples[kind]
                )
            ))
    |> ClaimCore.Json.JsonValue.Object
    |> codec.Read "$"
    |> Result.defaultWith (fun problem ->
        failtestf "Declared input fields do not admit: %A" problem)

let registration: RegistrationInput =
    groupInput
        ClaimCore.Wire.RequestCodecs.registration
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
        ClaimCore.Wire.RequestCodecs.decision
        "decision"
        (Map.ofList [ "paymentDecisionDate", "2026-08-15"; "payableAmount", "750.00" ])

let payment: PaymentInput =
    groupInput
        ClaimCore.Wire.RequestCodecs.payment
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

/// Authored act alternatives construct inputs; expectations stay with their behavior tests.
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

    CaseActKind.all
    |> List.map (fun kind ->
        let token = CaseActKind.token kind
        let payload = (CaseActKind.describe kind).Payload

        let members =
            match payload with
            | ActPayload.Bare _ -> []
            | ActPayload.Registration ->
                [ "values", RequestCodecs.registration.Write registration ]
            | ActPayload.RegistrationUpdate ->
                [
                    "values",
                    RequestCodecs.registrationUpdate.Write(registrationUpdate registration)
                ]
            | ActPayload.Decision -> [ "values", RequestCodecs.decision.Write decision ]
            | ActPayload.Payment -> [ "values", RequestCodecs.payment.Write payment ]
            | ActPayload.Reason -> [ "reason", JsonValue.String reason ]
            | ActPayload.Correction -> [ "groups", groups ]

        let act =
            JsonValue.Object(("kind", JsonValue.String token) :: members)
            |> RequestCodecs.caseAct.Read "$"
            |> Result.defaultWith (fun problem ->
                failtestf "Declared act input refused: %A" problem)

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
    (Vocabulary.entries AuthorityKind.vocabulary |> List.map snd)
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
