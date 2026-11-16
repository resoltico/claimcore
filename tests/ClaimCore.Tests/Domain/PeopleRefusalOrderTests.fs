module ClaimCore.Tests.PeopleRefusalOrderTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks

module T = ClaimCore.Tests.TaskBuilders

let private required result =
    result |> Result.defaultWith (fun issue -> failtestf "%A" issue)

let private owner = T.principal
let private other = T.newPrincipal

let private service =
    PrincipalKey.service "https://issuer.example/" "service" |> required

let private actor principal actorId roles cases : AccessView =
    {
        Actor =
            Some
                {
                    ActorId = actorId
                    Principal = principal
                    Enabled = true
                    Profile = None
                }
        Installation = roles
        Cases = Map.ofList cases
        HasScopedCases = not cases.IsEmpty
        HasAssignedRoles = not roles.IsEmpty || not cases.IsEmpty
    }

let private worker =
    actor
        owner
        T.actorId
        [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward ]
        []

type private Attempt = { Task: string; Inputs: InputMap }

type private PeopleRefusalScenario =
    {
        Name: string
        Caller: PrincipalKey * AccessView
        Attempt: Attempt
        Target: PrincipalKey * AccessView
        EnabledOwners: int
        Actors: int
        Current: CaseId option
        Expected: Refusal option
    }

let private state principal (view: AccessView) =
    view.Actor
    |> Option.map (fun actor ->
        let operation = OperationId.create (ActorId.value actor.ActorId) |> Option.get

        let stamp =
            { (T.context 1).Stamp with
                OperationId = operation
            }

        let profile =
            ActorProfile.parse
                {
                    DisplayName = "Synthetic target"
                    Contact = None
                }
            |> required

        let grants =
            [
                for role in view.Installation ->
                    {
                        Grant =
                            {
                                Role = role
                                Scope = Scope.Installation
                            }
                        Active = true
                    }
                for caseId, roles in view.Cases |> Map.toList do
                    for role in roles ->
                        {
                            Grant =
                                {
                                    Role = role
                                    Scope = Scope.Case caseId
                                }
                            Active = true
                        }
            ]

        let change =
            {
                PartChange.Aggregate = AggregateReference.Person actor.ActorId
                PartId = ActorId.value actor.ActorId
                Revision = Revision.first
                NaturalKey = Some(PrincipalKey.naturalKey principal)
                Value =
                    PartValue.Person
                        {
                            Principal = Some principal
                            Enabled = true
                            Profile = ProfileValue.Present(Some profile)
                            Grants = grants
                        }
                Erases = None
                Stamp = stamp
            }

        let current = Person.apply None change |> required

        if actor.Enabled then
            current
        else
            Person.apply
                (Some current)
                { change with
                    Revision = Revision.create 2L |> Option.get
                    NaturalKey = None
                    Stamp = (T.context 2).Stamp
                    Value =
                        PartValue.Person
                            {
                                Principal = None
                                Enabled = false
                                Profile = current.Profile
                                Grants = []
                            }
                }
            |> required)

let private ordering () =
    let manager = owner, worker
    let missingManager = owner, AccessView.empty

    let serviceManager =
        service, actor service (ActorId.ofOperationId (T.operation 603)) [ Role.Owner ] []

    let missingHuman = other, AccessView.empty
    let missingService = service, AccessView.empty
    let human = other, actor other (ActorId.ofOperationId (T.operation 602)) [] []

    let serviceActor =
        service, actor service (ActorId.ofOperationId (T.operation 603)) [] []

    let lastOwner =
        owner, actor owner (ActorId.ofOperationId (T.operation 1)) [ Role.Owner ] []

    let secondOwner =
        other, actor other (ActorId.ofOperationId (T.operation 602)) [ Role.Owner ] []

    let disabled =
        actor other (ActorId.ofOperationId (T.operation 602)) [ Role.Owner ] []

    let disabledOwner =
        other,
        { disabled with
            Actor = disabled.Actor |> Option.map (fun actor -> { actor with Enabled = false })
        }

    let knownCase = CaseId.ofOperationId (T.operation 604)
    let caseScope = GrantTarget.CaseReference "AUTH-SCOPE"

    let grantInputs role scope =
        [
            T.text InputTarget.Role (Role.token role)
            TaskInputId.Scope,
            InputValue.Text(
                if scope = GrantTarget.Installation then
                    "INSTALLATION"
                else
                    "CASE"
            )
            match scope with
            | GrantTarget.CaseReference reference ->
                T.text (InputTarget.Field BusinessField.CaseReference) reference
            | GrantTarget.CaseId id -> TaskInputId.CaseId, InputValue.Text(CaseId.format id)
            | GrantTarget.Installation -> ()
        ]
        |> T.inputs

    let grant role scope =
        {
            Task = "people.grant"
            Inputs = grantInputs role scope
        }

    let revoke role scope =
        {
            Task = "people.revoke"
            Inputs = grantInputs role scope
        }

    let register =
        {
            Task = "people.register"
            Inputs = T.inputs T.profileInputs
        }

    let enable =
        {
            Task = "people.enable"
            Inputs = InputMap.empty
        }

    let disable =
        {
            Task = "people.disable"
            Inputs = InputMap.empty
        }

    let provision =
        {
            Task = "people.provisionOwner"
            Inputs = T.inputs T.profileInputs
        }
    // Hand-authored expectations pin refusal priority, not the current implementation's output.
    // Actor counts include other unloaded actors; last-owner rows target the requesting owner itself.
    let rows =
        [
            {
                Name = "register missing human"
                Caller = manager
                Attempt = register
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "register existing human"
                Caller = manager
                Attempt = register
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "register missing service"
                Caller = manager
                Attempt = register
                Target = missingService
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "register existing service"
                Caller = manager
                Attempt = register
                Target = serviceActor
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "grant missing actor precedes role restriction"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = missingService
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorUnknown)
            }
            {
                Name = "revoke missing actor precedes missing case"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = missingHuman
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorUnknown)
            }
            {
                Name = "enable missing actor"
                Caller = manager
                Attempt = enable
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorUnknown)
            }
            {
                Name = "disable missing actor"
                Caller = manager
                Attempt = disable
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorUnknown)
            }
            {
                Name = "grant human owner"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "grant service owner"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.HumanPrincipalRequired)
            }
            {
                Name = "revoke service owner role even without a grant"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "missing case precedes human-only role"
                Caller = manager
                Attempt = grant Role.DataSteward caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "resolved case still requires human steward"
                Caller = manager
                Attempt = grant Role.DataSteward caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = Some(Refusal.People PeopleRefusal.HumanPrincipalRequired)
            }
            {
                Name = "service case handler"
                Caller = manager
                Attempt = grant Role.CaseHandler caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = None
            }
            {
                Name = "last installation owner revoke"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.LastOwner)
            }
            {
                Name = "second installation owner revoke"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = secondOwner
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "case grant revoke does not remove installation owner"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = None
            }
            {
                Name = "missing case precedes last-owner restriction"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "different role revoke does not remove owner"
                Caller = manager
                Attempt = revoke Role.CaseReader GrantTarget.Installation
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "last owner disable"
                Caller = manager
                Attempt = disable
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.LastOwner)
            }
            {
                Name = "second owner disable"
                Caller = manager
                Attempt = disable
                Target = secondOwner
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "disabled owner is not the last enabled owner"
                Caller = manager
                Attempt = disable
                Target = disabledOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "enable already enabled"
                Caller = manager
                Attempt = enable
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "grant human missing case"
                Caller = manager
                Attempt = grant Role.CaseReader caseScope
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "provision first human owner"
                Caller = manager
                Attempt = provision
                Target = missingHuman
                EnabledOwners = 0
                Actors = 0
                Current = None
                Expected = None
            }
            {
                Name = "provision first service owner"
                Caller = manager
                Attempt = provision
                Target = missingService
                EnabledOwners = 0
                Actors = 0
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.HumanPrincipalRequired)
            }
            {
                Name = "provision existing human installation"
                Caller = manager
                Attempt = provision
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "provision existing service installation identity precedes target rule"
                Caller = manager
                Attempt = provision
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "management precedes target existence"
                Caller = missingManager
                Attempt = grant Role.Owner caseScope
                Target = missingService
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "service owner grant conveys no management"
                Caller = serviceManager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }

        ]

    for scenario in rows do
        let principal, caller = scenario.Caller
        let targetPrincipal, target = scenario.Target
        let current = state targetPrincipal target
        let selected = Registry.find (TaskId.ofLiteral scenario.Attempt.Task) |> Option.get

        let task =
            match selected with
            | Task.Person task -> task
            | _ -> failtest "Expected person task."

        let cases =
            scenario.Current
            |> Option.map (fun caseId ->
                Map.ofList
                    [
                        GrantTarget.CaseId caseId, caseId
                        GrantTarget.CaseReference "AUTH-SCOPE", caseId
                    ])
            |> Option.defaultValue Map.empty

        let requesterPrincipal =
            if task.Declaration.Exposure = TaskExposure.Operator then
                targetPrincipal
            else
                principal

        let environment =
            {
                PersonEnvironment.Requester = caller
                RequesterPrincipal = requesterPrincipal
                Maintenance = false
                TargetCases = cases
                EnabledHumanOwners = scenario.EnabledOwners
                ActorCount = scenario.Actors
                TargetPrincipal = Some targetPrincipal
            }

        let original = T.context 605

        let context =
            { original with
                Decision =
                    { original.Decision with
                        Principal = requesterPrincipal
                        Access = caller
                        Lifetime = DecisionLifetime.Recorded
                    }
                Stamp =
                    if task.Declaration.Exposure = TaskExposure.Operator then
                        { original.Stamp with
                            ActorId = ActorId.ofOperationId original.Stamp.OperationId
                        }
                    else
                        original.Stamp
            }

        let values =
            if task.Declaration.Creates then
                let values =
                    T.principalInputs targetPrincipal
                    |> List.fold
                        (fun values (key, value) -> Map.add key value values)
                        scenario.Attempt.Inputs.Values

                let collections =
                    if scenario.Attempt.Task = "people.provisionOwner" then
                        Map.ofList [ TaskCollectionId.PeopleGrants, [] ]
                    else
                        Map.empty

                InputMap.create values collections |> Option.get
            else
                scenario.Attempt.Inputs

        let binding =
            if task.Declaration.Creates then
                Binding.PeopleNew Revision.zero
            else
                current
                |> Option.map (fun state -> Binding.People(state.Id, state.Revision))
                |> Option.defaultValue (
                    Binding.People(ActorId.ofOperationId (T.operation 999), Revision.first)
                )

        let result = task.Decide context environment current binding values

        let actual =
            match result with
            | Ok _ -> None
            | Error(Rejection.Refused reason) -> Some reason
            | other -> failtestf "%s: unexpected issue %A" scenario.Name other

        Expect.equal actual scenario.Expected scenario.Name

[<Tests>]
let tests =
    testCase "person tasks retain the hand-authored refusal priority matrix" ordering
