namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes

module internal PersonRules =
    let unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

    let newBinding =
        function
        | Binding.PeopleNew expected when expected = Revision.zero -> Ok()
        | _ ->
            Error(
                Refusal.invalidInput (InputTarget.ExpectedRevision, Violation.ExpectedRevisionRange)
            )

    let permission (declaration: TaskDeclaration) (environment: PersonEnvironment) =
        if
            Access.can
                environment.RequesterPrincipal
                environment.Requester
                (List.exactlyOne declaration.Capabilities)
                Scope.Installation
        then
            Ok()
        else
            Error unavailable

    let admit (context: TaskContext) (environment: PersonEnvironment) permitted gate =
        let decision =
            { context.Decision with
                Access = environment.Requester
                Maintenance = environment.Maintenance
            }

        DecisionContext.admit
            decision
            context.Stamp.OperationId
            (if decision.Principal <> environment.RequesterPrincipal then
                 Error unavailable
             else
                 permitted)
            gate

    let existing (environment: PersonEnvironment) (state: Person option) binding =
        match state, binding with
        | Some state, Binding.People(id, expected) when
            id = state.Id && environment.TargetPrincipal = Some state.Actor.Principal
            ->
            if expected <> state.Revision || Revision.value state.Revision = Revision.Maximum then
                Error(Refusal.People PeopleRefusal.ActorChanged)
            else
                Ok state
        | None, _ -> Error(Refusal.People PeopleRefusal.ActorUnknown)
        | _ -> Error unavailable

    let availability declaration (environment: PersonEnvironment) state binding =
        let admission =
            permission declaration environment
            |> Result.bind (fun () -> existing environment state binding |> Result.map ignore)

        let blocked =
            match admission with
            | Error refusal -> Some refusal
            | Ok() when environment.Maintenance -> Some Refusal.RegisterMaintenance
            | Ok() -> None

        Declarations.availability declaration blocked

    let registration
        (environment: PersonEnvironment)
        (state: Person option)
        binding
        principal
        provision
        =
        newBinding binding
        |> Result.bind (fun () ->
            if environment.TargetPrincipal |> Option.exists ((<>) principal) then
                Error unavailable
            elif state.IsSome || (provision && environment.ActorCount <> 0) then
                Error(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
            elif provision && not (PrincipalKey.isHuman principal) then
                Error(Refusal.People PeopleRefusal.HumanPrincipalRequired)
            else
                Ok())

    let scope (environment: PersonEnvironment) =
        function
        | GrantTarget.Installation -> Ok Scope.Installation
        | target ->
            match
                Map.tryFind (GrantTarget.canonical target) environment.TargetCases
                |> Option.orElseWith (fun () -> Map.tryFind target environment.TargetCases)
            with
            | Some id when
                (match target with
                 | GrantTarget.CaseId expected -> expected = id
                 | _ -> true)
                ->
                Ok(Scope.Case id)
            | _ -> Error unavailable

    let grantAdmission principal grant =
        Access.grantRefusal principal grant
        |> Option.map (Refusal.People >> Error)
        |> Option.defaultValue (Ok())

    let resolveGrant environment (grant: GrantInput) =
        scope environment grant.Scope
        |> Result.map (fun scope ->
            {
                ActorGrant.Role = grant.Role
                Scope = scope
            })

    let admitGrants environment principal grants =
        grants
        |> List.map (fun grant ->
            resolveGrant environment grant
            |> Result.bind (fun grant ->
                grantAdmission principal grant |> Result.map (fun () -> grant)))
        |> ClaimCore.Domain.Inputs.Parsing.collect
        |> Result.bind (fun grants ->
            if List.distinct grants <> grants then
                Error(Refusal.invalidInput (InputTarget.Grants, Violation.DuplicateGrant))
            else
                Ok grants)

    let owner (state: Person) =
        Access.isEnabledHumanOwner (Person.access state)

    let protectOwner environment state =
        if owner state && environment.EnabledHumanOwners <= 1 then
            Error(Refusal.People PeopleRefusal.LastOwner)
        else
            Ok()

    let createValue (context: TaskContext) principal (profile: ProfileValue) grants =
        let id = ActorId.ofOperationId context.Stamp.OperationId

        {
            Aggregate = AggregateReference.Person id
            PartId = ActorId.value id
            Revision = Revision.first
            NaturalKey = Some(PrincipalKey.naturalKey principal)
            Value =
                PartValue.Person
                    {
                        Principal = Some principal
                        Enabled = true
                        Grants = grants |> List.map (fun grant -> { Grant = grant; Active = true })
                        Profile = profile
                    }
            Erases = None
            Stamp = context.Stamp
        }

    let create context principal profile grants =
        createValue context principal (ProfileValue.Present(Some profile)) grants

    let change (context: TaskContext) (state: Person) enabled profile grants =
        {
            Aggregate = AggregateReference.Person state.Id
            PartId = ActorId.value state.Id
            Revision = Revision.successor state.Revision
            NaturalKey = None
            Value =
                PartValue.Person
                    {
                        Principal = None
                        Enabled = enabled
                        Grants = grants
                        Profile = profile
                    }
            Erases = None
            Stamp = context.Stamp
        }
