module ClaimCore.Domain.Tasks.People.Disable

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.disable"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Stewardship
        [ Capability.ManageAccess ]
        [ operation; required TaskInputId.ActorId |> bound; revision ]
        []

type private Command = { Enabled: bool }
let check (_: InputMap) = []

let availability environment state binding =
    let offered = PersonRules.availability declaration environment state binding

    match offered.Blocked, state with
    | None, Some state ->
        match PersonRules.protectOwner environment state with
        | Ok() -> offered
        | Error refusal -> { offered with Blocked = Some refusal }
    | _ -> offered

let decide context environment (state: Person option) binding (_: InputMap) =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.bind (fun state ->
        PersonRules.protectOwner environment state
        |> Result.map (fun () ->
            let command = { Enabled = false }
            [ PersonRules.change context state command.Enabled state.Profile [] ]))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
