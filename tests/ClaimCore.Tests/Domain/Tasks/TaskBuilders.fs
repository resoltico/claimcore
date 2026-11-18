module ClaimCore.Tests.TaskBuilders

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks

let accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected acceptance, got %A" error

let operation number =
    Guid.Parse(sprintf "01a1dffe-778b-7000-8000-%012d" number)
    |> OperationId.create
    |> Option.get

let actorId =
    ActorId.create (Guid.Parse "00000000-0000-4000-8000-000000000001") |> Option.get

let principal =
    PrincipalKey.human "https://issuer.example/" "task-scenarios" |> accepted

let actor =
    {
        ActorId = actorId
        Principal = principal
        Enabled = true
        Profile = None
    }

let access =
    AccessView.ofActor (
        Some
            {
                Actor = actor
                Grants =
                    [
                        Role.Owner
                        Role.CaseHandler
                        Role.PaymentRecorder
                        Role.DataSteward
                        Role.Auditor
                    ]
                    |> List.map (fun role ->
                        {
                            Role = role
                            Scope = Scope.Installation
                        })
            }
    )

let environment =
    {
        FamilyEnvironment.Requester = access
        RequesterPrincipal = principal
        Maintenance = false
        ReferenceOwner = fun _ -> None
        AssigneeEligible = fun _ -> true
    }

let context number =
    let id = operation number
    let observedAt = OperationId.issuedAt id |> fun instant -> instant.AddSeconds 1.0

    {
        TaskContext.Stamp =
            {
                Position =
                    EffectPosition.create (Sequence.create (int64 number) |> Option.get) 0
                    |> Option.get
                OperationId = id
                Authority = Authority.Actor actorId
                RecordedAt = observedAt
            }
        Decision =
            {
                BusinessDate = Some(DateOnly(2026, 11, 15))
                ObservedAt = observedAt
                Principal = principal
                Access = access
                Maintenance = false
                Lifetime = DecisionLifetime.Execution None
            }
    }

let reference = CaseReference.parse "TASK-1" |> accepted

let registration =
    RegistrationInput.create
        [
            BusinessField.IncidentDate, "2026-01-01"
            BusinessField.ReceivedDate, "2026-01-02"
            BusinessField.IncidentCountry, "LV"
            BusinessField.PolicyNumber, "Claimant"
            BusinessField.InsurerClaimReference, "Insurer"
            BusinessField.ClaimedAmount, "1000.00"
            BusinessField.ClaimedCurrency, "EUR"
        ]
    |> accepted
    |> Registration.parse
    |> accepted

let initialChange =
    {
        Aggregate = AggregateReference.Family(CaseId.ofOperationId (operation 1))
        PartId = OperationId.value (operation 1)
        Revision = Revision.first
        NaturalKey = Some "TASK-1"
        Value =
            PartValue.Case
                {
                    Disposition = None
                    ReopenReason = None
                    Reference = Some reference
                    Lifecycle = Lifecycle.Active
                    Handling = Handling.Open
                    Content =
                        Some
                            {
                                Registration = registration
                                Finance = FinancialPosition.undecided
                            }
                }
        Erases = None
        Stamp = (context 1).Stamp
    }

let family () =
    CaseFamily.apply None initialChange |> accepted

let target (state: CaseFamily) = CaseSelector.Id state.Id

let inputs values =
    InputMap.create (Map.ofList values) Map.empty |> Option.get

let text target value =
    TaskInputId.Target target, InputValue.Text value

let planInputs action due =
    inputs [ text InputTarget.ReminderAction action; text InputTarget.ReminderDueDate due ]

let apply state changes =
    changes
    |> List.fold (fun state change -> Some(CaseFamily.apply state change |> accepted)) (Some state)
    |> Option.get

let closingInputs outcome =
    inputs [ text (InputTarget.Field BusinessField.Outcome) outcome ]

let reopeningInputs reason =
    inputs [ text InputTarget.Reason reason ]

let setCase lifecycle status (state: CaseFamily) =
    let change =
        { initialChange with
            Revision = Revision.successor state.Case.Revision
            NaturalKey = None
            Stamp = (context (int (Sequence.value state.Sequence) + 1)).Stamp
            Value =
                PartValue.Case
                    {
                        CaseValue.Reference = None
                        Lifecycle = lifecycle
                        Handling =
                            if status = CaseStatus.Opened then
                                Handling.Open
                            else
                                Handling.Closed
                                    {
                                        Outcome = ClosureOutcome.Settled
                                        Note = None
                                    }
                        Content =
                            if lifecycle = Lifecycle.Erased then
                                None
                            else
                                state.Case.Value.Content
                        Disposition = None
                        ReopenReason = None
                    }
            Erases =
                if lifecycle = Lifecycle.Erased then
                    Some(CompartmentOwner.Family state.Id)
                else
                    None
        }

    apply state [ change ]

let reminder () =
    let state = family ()

    let changes =
        ClaimCore.Domain.Tasks.Reminder.Create.decide
            (context 2)
            environment
            (Some(DecisionFacts.fromReplay state))
            (Binding.ReminderNew(target state))
            (planInputs "Request photographs" "2026-11-16")
        |> accepted

    apply state changes, ReminderId.ofOperationId (operation 2)

let reminderBinding state id revision =
    Binding.Reminder(target state, id, revision)

let expectRefusal expected result =
    match result with
    | Error(Rejection.Refused actual) -> Expect.equal actual expected "Business refusal"
    | actual -> failtestf "Expected refusal %A, got %A" expected actual

let expectInputs result =
    match result with
    | Error(Rejection.Input issues) -> TaskInputIssues.toList issues
    | actual -> failtestf "Expected input issues, got %A" actual

let newPrincipal =
    PrincipalKey.human "https://issuer.example/" "new-person" |> accepted

let peopleEnvironment target =
    {
        PersonEnvironment.Requester = access
        RequesterPrincipal = principal
        Maintenance = false
        TargetCases = Map.empty
        EnabledHumanOwners = 2
        ActorCount = 1
        TargetPrincipal = Some target
    }

let principalInputs principal =
    let kind, issuer, subject = PrincipalKey.parts principal

    [
        text InputTarget.PrincipalKind (Vocabulary.token PrincipalKind.vocabulary kind)
        text InputTarget.PrincipalIssuer issuer
        text InputTarget.PrincipalSubject subject
    ]

let profileInputs =
    [
        text InputTarget.ProfileDisplayName "New Person"
        text InputTarget.ProfileContact "contact@example.test"
    ]

let peopleInputs principal =
    inputs (principalInputs principal @ profileInputs)

let personApply state changes =
    changes
    |> List.fold (fun state change -> Some(Person.apply state change |> accepted)) (Some state)
    |> Option.get

let personWith principal =
    let changes =
        ClaimCore.Domain.Tasks.People.Register.decide
            (context 2)
            (peopleEnvironment principal)
            None
            (Binding.PeopleNew Revision.zero)
            (peopleInputs principal)
        |> accepted

    Person.apply None changes.Head |> accepted

let person () = personWith newPrincipal

let personBinding (state: Person) =
    Binding.People(state.Id, state.Revision)

let grantInputs role scope =
    inputs
        [
            text InputTarget.Role (Role.token role)
            TaskInputId.Scope, InputValue.Text scope
        ]

let owner () =
    let environment =
        { peopleEnvironment newPrincipal with
            ActorCount = 0
            EnabledHumanOwners = 0
            Requester = AccessView.empty
        }

    let changes =
        ClaimCore.Domain.Tasks.People.ProvisionOwner.decide
            (context 2)
            environment
            None
            (Binding.PeopleNew Revision.zero)
            (peopleInputs newPrincipal)
        |> accepted

    Person.apply None changes.Head |> accepted

let openingInputs =
    RegistrationInput.values (Registration.authored registration)
    |> List.map (fun (field, value) -> text (InputTarget.Field field) value)
    |> fun values ->
        inputs (text (InputTarget.Field BusinessField.CaseReference) "TASK-1" :: values)

let caseBinding (state: CaseFamily) =
    Binding.Case(target state, state.Case.Revision)

let decisionInputs date amount currency =
    inputs
        [
            text (InputTarget.Field BusinessField.PaymentDecisionDate) date
            text (InputTarget.Field BusinessField.PayableAmount) amount
            text (InputTarget.Field BusinessField.PayableCurrency) currency
        ]

let paymentInputs date amount currency =
    inputs
        [
            text (InputTarget.Field BusinessField.PaymentDate) date
            text (InputTarget.Field BusinessField.PaidAmount) amount
            text (InputTarget.Field BusinessField.PaidCurrency) currency
        ]

let decided () =
    let state = family ()

    ClaimCore.Domain.Tasks.Case.Decide.decide
        (context 2)
        environment
        (Some(DecisionFacts.fromReplay state))
        (caseBinding state)
        (decisionInputs "2026-02-01" "1000" "EUR")
    |> accepted
    |> apply state

let pay number date amount (state: CaseFamily) =
    ClaimCore.Domain.Tasks.Case.RecordPayment.decide
        (context number)
        environment
        (Some(DecisionFacts.fromReplay state))
        (caseBinding state)
        (paymentInputs date amount "EUR")
    |> accepted
    |> apply state

let correction registration decision values =
    inputs (
        text (InputTarget.Group RecordGroup.Registration) registration
        :: text (InputTarget.Group RecordGroup.Decision) decision
        :: values
    )
