module ClaimCore.Domain.Tasks.People.Enable

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.enable"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        [ operation; required TaskInputId.ActorId |> bound; revision ]
        []

type private Command = { Enabled: bool }
let check (_: InputMap) = []

let availability environment state binding =
    PersonRules.availability declaration environment state binding

let decide context environment (state: Person option) binding (_: InputMap) =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.bind (fun state ->
        Ok()
        |> Result.map (fun () ->
            let command = { Enabled = true }
            [ PersonRules.change context state command.Enabled state.Profile [] ]))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Authorize =
                fun principal access _ -> PersonRules.permissionFor declaration principal access
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
