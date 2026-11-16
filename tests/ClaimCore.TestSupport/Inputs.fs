module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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
    |> Result.defaultWith invalidOp

/// A complete transport fixture; policy tests assert literal reasons from the actual service.
let caseAdvice state =
    let headline =
        state
        |> CaseState.map (fun (fields: CaseFields) ->
            fields.Status, CaseProgress.ofFinancial fields.Finance)
        |> CaseHeadline.ofState

    let blocked =
        (ClaimCore.Domain.Tasks.Registry.clientDeclarations
         |> List.filter (fun declaration -> declaration.Target = ActSubject.Case)
         |> List.map _.Id)
        |> List.map (fun act ->
            {
                CaseTaskRefusal.Act = act
                Reason = Refusal.Case(CaseRefusal.NotOffered headline)
            })

    CaseAdvice.create [] None HistoryAccess.None blocked
    |> Result.defaultWith invalidOp

/// Wire codecs own authored input membership and ordering; Domain supplies scalar metadata.
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

let caseListPage page =
    {
        CaseListRequest.Page = page
        Filter = CaseListFilter.empty<string>
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

/// Build a disclosed control for interaction tests; business availability has task-owned tests.
let offeredTask token binding seeds =
    let task = TaskId.ofLiteral token
    let declaration = ClaimCore.Domain.Tasks.Registry.findDeclaration task |> Option.get

    TaskOffer.Available(
        task,
        binding,
        {
            Seeds = seeds
            Selections = Set.empty
            Editable =
                declaration.Inputs
                |> List.filter (fun input -> input.Source = InputSource.Authored)
                |> List.map _.Input
            Choices = Map.empty
            Blocked = None
            Recommendation = None
        }
    )
