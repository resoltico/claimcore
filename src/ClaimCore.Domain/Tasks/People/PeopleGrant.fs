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
        (operation
         :: (required TaskInputId.ActorId |> bound)
         :: revision
         :: grantAdmission)
        []

type private Command = { Grant: GrantInput }

let private parse inputs =
    PersonInputs.grant inputs |> Result.map (fun grant -> { Grant = grant })

let check inputs = Parsing.check parse inputs

let availability environment (state: Person option) binding =
    let offered = PersonRules.availability declaration environment state binding

    let roles =
        Role.all
        |> List.filter (fun role ->
            state
            |> Option.exists (fun person -> PrincipalKey.isHuman person.Actor.Principal)
            || not (Access.humanOnlyRole role))
        |> List.map Role.token

    { offered with
        Seeds =
            Map.ofList
                [ TaskInputId.Scope, InputValue.Text(ScopeKind.token ScopeKind.Installation) ]
        Choices = Map.ofList [ TaskInputId.Target InputTarget.Role, roles ]
    }

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
            Authorize =
                fun principal access _ -> PersonRules.permissionFor declaration principal access
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decide
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
