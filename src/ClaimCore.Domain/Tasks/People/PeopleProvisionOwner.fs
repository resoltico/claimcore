module ClaimCore.Domain.Tasks.People.ProvisionOwner

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let initialRoles =
    [ Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward; Role.Auditor ]

let declaration =
    { create
          "people.provisionOwner"
          AggregateKind.Person
          ActSubject.People
          true
          ActFamily.Business
          Consequence.Routine
          []
          (operation :: principal @ profile)
          [
              {
                  Collection = TaskCollectionId.PeopleGrants
                  MinimumRows = 0
                  Inputs = grantAdmission
                  RowConstraints = []
              }
          ] with
        Exposure = TaskExposure.Operator
    }

type private Command =
    {
        Principal: PrincipalKey
        Profile: ActorProfile
        Roles: Role list
    }

let validateRoles roles =
    if List.distinct roles <> roles then
        Error(Refusal.invalidInput (InputTarget.Grants, Violation.DuplicateGrant))
    elif roles |> List.exists (fun role -> not (List.contains role initialRoles)) then
        Error(Refusal.People PeopleRefusal.RoleScopeNotApplicable)
    else
        Ok roles

let private roles inputs =
    PersonInputs.grants declaration.Collections.Head.MinimumRows inputs
    |> Result.bind (fun grants ->
        if grants |> List.exists (fun grant -> grant.Scope <> GrantTarget.Installation) then
            Error(Refusal.People PeopleRefusal.RoleScopeNotApplicable)
        else
            validateRoles (grants |> List.map _.Role))

let private parse inputs =
    Admission.map2
        (fun (principal, profile) roles ->
            {
                Principal = principal
                Profile = profile
                Roles = roles
            })
        (Admission.map2
            (fun principal profile -> principal, profile)
            (PersonInputs.principal inputs)
            (PersonInputs.profile inputs))
        (roles inputs)

let check inputs = Parsing.check parse inputs

let availability (environment: PersonEnvironment) (state: Person option) binding =
    let blocked =
        if Result.isError (PersonRules.newBinding binding) then
            Some(
                Refusal.invalidInput (InputTarget.ExpectedRevision, Violation.ExpectedRevisionRange)
            )
        elif environment.ActorCount <> 0 || state.IsSome then
            Some(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
        elif environment.Maintenance then
            Some Refusal.RegisterMaintenance
        else
            None

    Declarations.availability declaration blocked

let decide context environment (state: Person option) binding inputs =
    PersonRules.admit context environment (Ok()) (fun () -> Ok())
    |> Result.bind (fun () -> parse inputs)
    |> Result.bind (fun command ->
        PersonInputs.admitProfile context command.Profile
        |> Result.bind (fun profile ->
            PersonRules.registration environment state binding command.Principal true
            |> Result.map (fun () ->
                let grants =
                    Role.Owner :: command.Roles
                    |> List.map (fun role ->
                        {
                            ActorGrant.Role = role
                            Scope = Scope.Installation
                        })

                [ PersonRules.create context command.Principal profile grants ])))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let decideRetained context environment state binding inputs =
    PersonRules.admit context environment (Ok()) (fun () -> Ok())
    |> Result.bind (fun () ->
        Admission.map2
            (fun principal roles -> principal, roles)
            (PersonInputs.principal inputs)
            (roles inputs))
    |> Result.bind (fun (principal, roles) ->
        PersonRules.registration environment state binding principal true
        |> Result.map (fun () ->
            let grants =
                Role.Owner :: roles
                |> List.map (fun role ->
                    {
                        ActorGrant.Role = role
                        Scope = Scope.Installation
                    })

            [ PersonRules.createValue context principal (ProfileValue.Present None) grants ]))
    |> Parsing.reject
    |> Result.map (TaskContext.changes context)

let task =
    Task.Person
        {
            Declaration = declaration
            Authorize = fun _ _ _ -> Ok()
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = fun _ _ -> true
            Describe = PartDescriptions.person
        }
