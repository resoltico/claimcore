module ClaimCore.Domain.Tasks.People.Revoke

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.revoke"
        AggregateKind.Person
        ActSubject.People
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        (operation
         :: (required TaskInputId.ActorId |> bound)
         :: revision
         :: grantSelection)
        []

type private Command = { Grant: GrantInput }

let private parse inputs =
    PersonInputs.grant inputs |> Result.map (fun grant -> { Grant = grant })

let check inputs = Parsing.check parse inputs

let availability environment (state: Person option) binding =
    let offered = PersonRules.availability declaration environment state binding

    let selections =
        if offered.Blocked.IsSome then
            Set.empty
        else
            state
            |> Option.toList
            |> List.collect (fun state ->
                state.Grants
                |> Map.keys
                |> Seq.map (fun grant ->
                    [
                        target InputTarget.Role, InputValue.Text(Role.token grant.Role)
                        TaskInputId.Scope,
                        InputValue.Text(
                            ScopeKind.token (
                                match grant.Scope with
                                | Scope.Installation -> ScopeKind.Installation
                                | Scope.Case _ -> ScopeKind.Case
                            )
                        )
                        match grant.Scope with
                        | Scope.Case id -> TaskInputId.CaseId, InputValue.Text(CaseId.format id)
                        | Scope.Installation -> ()
                    ]
                    |> Map.ofList)
                |> Seq.toList)
            |> Set.ofList

    { offered with
        Seeds =
            Map.ofList
                [ TaskInputId.Scope, InputValue.Text(ScopeKind.token ScopeKind.Installation) ]
        Selections = selections
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
                // Revoking an absent grant is accepted; active-grant admission does not apply here.
                let protectedOwner =
                    if grant.Role = Role.Owner && grant.Scope = Scope.Installation then
                        PersonRules.protectOwner environment state
                    else
                        Ok()

                protectedOwner
                |> Result.map (fun () ->
                    [
                        PersonRules.change
                            context
                            state
                            state.Actor.Enabled
                            state.Profile
                            [ { Grant = grant; Active = false } ]
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
