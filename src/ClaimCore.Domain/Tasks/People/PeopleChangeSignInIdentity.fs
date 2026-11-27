module ClaimCore.Domain.Tasks.People.ChangeSignInIdentity

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.changeSignInIdentity"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        (operation
         :: (required TaskInputId.ActorId |> bound)
         :: revision
         :: signInIdentity)
        []

let private handover (environment: PersonEnvironment) (target: Person) =
    if
        PersonRules.owner target
        && (environment.Requester.Actor
            |> Option.exists (fun actor -> actor.ActorId = target.Id))
    then
        Error(Refusal.People PeopleRefusal.DistinctOwnerRequired)
    else
        Ok()

let private availability environment state binding =
    let offered = PersonRules.availability declaration environment state binding

    match offered.Blocked, state with
    | None, Some target ->
        match handover environment target with
        | Ok() -> offered
        | Error refusal -> { offered with Blocked = Some refusal }
    | _ -> offered

let private decide context environment state binding inputs =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.bind (fun state ->
        let kind, _, _ = PrincipalKey.parts state.Actor.Principal

        PersonInputs.identity kind inputs
        |> Result.bind (fun principal ->
            if principal = state.Actor.Principal then
                Error(Refusal.People PeopleRefusal.NoChanges)
            else
                PersonRules.signInIdentity state principal environment
                |> Result.bind (fun naturalKey ->
                    handover environment state |> Result.map (fun () -> naturalKey))
                |> Result.map (fun naturalKey ->
                    [
                        PersonRules.changeIdentity
                            context
                            state
                            state.Actor.Enabled
                            []
                            principal
                            naturalKey
                    ])))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Authorize =
                fun principal access _ -> PersonRules.permissionFor declaration principal access
            Check = Parsing.check (PersonInputs.identity PrincipalKind.Human)
            Availability = availability
            DecisionFactsAvailable = fun _ _ -> true
            Describe = ClaimCore.Domain.Changes.PartDescriptions.person
            Decide = decide
            DecideRetained = decide
        }
