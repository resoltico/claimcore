module ClaimCore.Domain.Tasks.People.RecoverOwner

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    { create
          "people.recoverOwner"
          AggregateKind.Person
          ActSubject.People
          false
          ActFamily.Business
          Consequence.Routine
          []
          (operation :: signInIdentity
           @ [ optional (target InputTarget.ProfileDisplayName) ])
          [] with
        Exposure = TaskExposure.Operator
    }

let private ownerGrant =
    {
        ActorGrant.Role = Role.Owner
        Scope = Scope.Installation
    }

let private check = Parsing.check (PersonInputs.identity PrincipalKind.Human)

let private availability (environment: PersonEnvironment) (state: Person option) binding =
    let blocked =
        if environment.ActorCount = 0 then
            Some(Refusal.People PeopleRefusal.ActorUnknown)
        else
            let admitted =
                match binding with
                | Binding.PeopleNew _ -> PersonRules.newBinding binding
                | _ -> PersonRules.existing environment state binding |> Result.map ignore

            match admitted with
            | Ok() -> None
            | Error refusal -> Some refusal

    Declarations.availability declaration blocked

let private decide retained context (environment: PersonEnvironment) state binding inputs =
    let gate () =
        (availability environment state binding).Blocked
        |> Option.map Error
        |> Option.defaultValue (Ok())

    PersonRules.admit context { environment with Maintenance = false } (Ok()) gate
    |> Result.bind (fun () -> PersonInputs.identity PrincipalKind.Human inputs)
    |> Result.bind (fun principal ->
        match state, binding with
        | None, Binding.PeopleNew _ ->
            if environment.PrincipalOwner.IsSome then
                Error(Refusal.People PeopleRefusal.IdentityReserved)
            elif retained then
                Ok
                    [
                        PersonRules.createValue
                            context
                            principal
                            (ProfileValue.Present None)
                            [ ownerGrant ]
                    ]
            else
                PersonInputs.profile inputs
                |> Result.bind (PersonInputs.admitProfile context)
                |> Result.map (fun profile ->
                    [ PersonRules.create context principal profile [ ownerGrant ] ])
        | Some person, Binding.People _ ->
            if not (PrincipalKey.isHuman person.Actor.Principal) then
                Error(Refusal.People PeopleRefusal.HumanPrincipalRequired)
            else
                let grants =
                    if Map.containsKey ownerGrant person.Grants then
                        []
                    else
                        [ { Grant = ownerGrant; Active = true } ]

                if principal = person.Actor.Principal then
                    if person.Actor.Enabled && grants.IsEmpty then
                        Error(Refusal.People PeopleRefusal.NoChanges)
                    else
                        Ok [ PersonRules.change context person true person.Profile grants ]
                else
                    PersonRules.signInIdentity person principal environment
                    |> Result.map (fun naturalKey ->
                        [
                            PersonRules.changeIdentity
                                context
                                person
                                true
                                grants
                                principal
                                naturalKey
                        ])
        | _ -> Error PersonRules.unavailable)
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Authorize = fun _ _ _ -> Ok()
            Availability = availability
            Check = check
            DecisionFactsAvailable = fun _ _ -> true
            Decide = decide false
            DecideRetained = decide true
            Describe = PartDescriptions.person
        }
