module ClaimCore.Domain.Tasks.People.EraseProfile

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.eraseProfile"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Irreversible
        [ Capability.ManageAccess ]
        [ operation; required TaskInputId.ActorId |> bound; revision ]
        []

type private Command = { Owner: CompartmentOwner }
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
    |> Result.map (fun state ->
        let command =
            {
                Owner = CompartmentOwner.Profile state.Id
            }

        [
            { PersonRules.change context state state.Actor.Enabled ProfileValue.Absent [] with
                Erases = Some command.Owner
            }
        ])
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
