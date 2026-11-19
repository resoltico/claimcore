module ClaimCore.Domain.Tasks.People.Admit

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    create
        "people.admit"
        AggregateKind.Person
        ActSubject.People
        true
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageAccess ]
        (operation :: principal @ profile)
        [
            {
                Collection = TaskCollectionId.PeopleGrants
                MinimumRows = PeopleAdmission.MinimumGrants
                Inputs = grantAdmission
                RowConstraints = []
            }
        ]

type private Command =
    {
        Principal: PrincipalKey
        Profile: ActorProfile
        Grants: GrantInput list
    }

let private parse inputs =
    Admission.map2
        (fun (principal, profile) grants ->
            {
                Principal = principal
                Profile = profile
                Grants = grants
            })
        (Admission.map2
            (fun principal profile -> principal, profile)
            (PersonInputs.principal inputs)
            (PersonInputs.profile inputs))
        (PersonInputs.grants declaration.Collections.Head.MinimumRows inputs)

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    let allowed =
        PersonRules.permission declaration environment
        |> Result.bind (fun () -> PersonRules.newBinding binding)
        |> Result.bind (fun () ->
            match state with
            | Some _ -> Error(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            | None -> Ok())

    Declarations.availability
        declaration
        (match allowed with
         | Error refusal -> Some refusal
         | Ok() when environment.Maintenance -> Some Refusal.RegisterMaintenance
         | Ok() -> None)

let decide context environment (state: Person option) binding inputs =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () -> parse inputs)
    |> Result.bind (fun command ->
        PersonInputs.admitProfile context command.Profile
        |> Result.bind (fun profile ->
            PersonRules.registration environment state binding command.Principal false
            |> Result.bind (fun () ->
                PersonRules.admitGrants environment command.Principal command.Grants)
            |> Result.map (fun grants ->
                [ PersonRules.create context command.Principal profile grants ])))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let decideRetained context environment state binding inputs =
    PersonRules.admit
        context
        environment
        (PersonRules.permission declaration environment)
        (fun () -> Ok())
    |> Result.bind (fun () ->
        Admission.map2
            (fun principal grants -> principal, grants)
            (PersonInputs.principal inputs)
            (PersonInputs.grants declaration.Collections.Head.MinimumRows inputs))
    |> Result.bind (fun (principal, grants) ->
        PersonRules.registration environment state binding principal false
        |> Result.bind (fun () -> PersonRules.admitGrants environment principal grants)
        |> Result.map (fun grants ->
            [ PersonRules.createValue context principal (ProfileValue.Present None) grants ]))
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
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
