module ClaimCore.Domain.Tasks.People.Grant

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.grant"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        (operation :: (required TaskInputId.ActorId |> bound) :: revision :: grant)
        []

type private Command = { Grant: AuthoredGrant }

let private parse inputs =
    PersonInputs.grant inputs |> Result.map (fun grant -> { Grant = grant })

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    PersonRules.availability declaration environment state binding

let decide context environment (state: Person option) binding inputs =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> PersonRules.existing environment state binding)
    |> Result.bind (fun state ->
        parse inputs
        |> Result.bind (fun command ->
            PersonRules.resolveGrant environment command.Grant
            |> Result.bind (fun grant ->
                PersonRules.grantAdmission state.Actor.Principal grant
                |> Result.map (fun () ->
                    [
                        PersonRules.change
                            context
                            state
                            state.Actor.Enabled
                            state.Profile
                            [ { Grant = grant; Active = true } ]
                    ]))))
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
