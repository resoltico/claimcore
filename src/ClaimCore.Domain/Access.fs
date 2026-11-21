namespace ClaimCore.Domain

/// Potential sources remain visible for disabled actors; Effective reflects current actor validity.
type CapabilityAccess =
    {
        Capability: Capability
        DirectRoles: Role list
        InstallationRoles: Role list
        Effective: bool
    }

/// A case assignee is an enabled human with every declared case capability.
[<RequireQualifiedAccess>]
type AssigneeSelection = EnabledHumanWithAll of Capability list

module Access =
    // Roles, principal restrictions and scoped capabilities have one domain owner.
    let policy =
        function
        | Role.Owner -> [ Capability.ManageAccess; Capability.ReadAccess ]
        | Role.CaseReader -> [ Capability.ReadCase; Capability.ReadHistory ]
        | Role.CaseRegistrar -> [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
        | Role.CaseHandler ->
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.Register
                Capability.Decide
                Capability.Close
                Capability.ManageCaseWork
            ]
        | Role.PaymentRecorder ->
            [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
        | Role.DataSteward -> [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
        | Role.Auditor ->
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.ReadAccess
                Capability.ReadDisclosureTrail
            ]

    let roles = Role.all

    let rolesFor capability =
        roles |> List.filter (fun role -> List.contains capability (policy role))

    /// Scope meaning belongs to capability policy, independently of principal-kind admission.
    let capabilityScopes =
        function
        | Capability.ManageAccess
        | Capability.ReadAccess
        | Capability.ReadDisclosureTrail -> [ ScopeKind.Installation ]
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ManageCaseWork
        | Capability.Steward -> ScopeKind.all

    let roleScopes role =
        ScopeKind.all
        |> List.filter (fun scope ->
            policy role
            |> List.exists (fun capability -> List.contains scope (capabilityScopes capability)))

    /// Capability restrictions apply when exercising a right, not to every capability of its role.
    let humanOnlyCapability =
        function
        | Capability.Steward
        | Capability.ManageCaseWork
        | Capability.ManageAccess -> true
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ReadAccess
        | Capability.ReadDisclosureTrail -> false

    let humanOnlyRole =
        function
        | Role.Owner
        | Role.DataSteward -> true
        | Role.CaseReader
        | Role.CaseRegistrar
        | Role.CaseHandler
        | Role.PaymentRecorder
        | Role.Auditor -> false

    let invalidRoles principal =
        if PrincipalKey.isHuman principal then
            []
        else
            roles |> List.filter humanOnlyRole

    let readRoles principal =
        rolesFor Capability.ReadCase
        |> List.filter (fun role -> PrincipalKey.isHuman principal || not (humanOnlyRole role))

    let grantRefusal principal (grant: ActorGrant) =
        let kind =
            match grant.Scope with
            | Scope.Installation -> ScopeKind.Installation
            | Scope.Case _ -> ScopeKind.Case

        if humanOnlyRole grant.Role && not (PrincipalKey.isHuman principal) then
            Some PeopleRefusal.HumanPrincipalRequired
        elif not (List.contains kind (roleScopes grant.Role)) then
            Some PeopleRefusal.RoleScopeNotApplicable
        else
            None

    let validActor principal (view: AccessView) =
        let loaded =
            view.Installation @ (view.Cases |> Map.values |> Seq.collect id |> Seq.toList)

        view.Actor
        |> Option.exists (fun actor ->
            actor.Enabled
            && actor.Principal = principal
            && (PrincipalKey.isHuman principal || loaded |> List.forall (humanOnlyRole >> not)))

    let can principal (view: AccessView) capability resource =
        let scoped =
            match resource with
            | Scope.Installation -> Some view.Installation
            | Scope.Case id when List.contains ScopeKind.Case (capabilityScopes capability) ->
                Some(view.Installation @ (view.Cases |> Map.tryFind id |> Option.defaultValue []))
            | _ -> None

        validActor principal view
        && (not (humanOnlyCapability capability) || PrincipalKey.isHuman principal)
        && (scoped
            |> Option.exists (List.exists (fun role -> List.contains capability (policy role))))

    let assigneeSelection =
        AssigneeSelection.EnabledHumanWithAll [ Capability.ReadCase; Capability.ManageCaseWork ]

    let matchesAssignee selection principal view caseId =
        match selection with
        | AssigneeSelection.EnabledHumanWithAll capabilities ->
            PrincipalKey.isHuman principal
            && validActor principal view
            && capabilities
               |> List.forall (fun capability -> can principal view capability (Scope.Case caseId))

    let eligibleAssignee principal view caseId =
        matchesAssignee assigneeSelection principal view caseId

    let isEnabledHumanOwner (actor: ActorAccess) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains
            {
                ActorGrant.Role = Role.Owner
                Scope = Scope.Installation
            }
            actor.Grants

    /// Explain only the supplied resource snapshot; this does not predict business-act eligibility.
    let explain principal (view: AccessView) resource =
        let kind, direct, inherited =
            match resource with
            | Scope.Installation -> ScopeKind.Installation, view.Installation, []
            | Scope.Case id ->
                ScopeKind.Case,
                (view.Cases |> Map.tryFind id |> Option.defaultValue []),
                view.Installation

        Capability.all
        |> List.choose (fun capability ->
            let sources roles =
                roles
                |> List.filter (fun role -> List.contains capability (policy role))
                |> List.distinct

            let directRoles = sources direct
            let installationRoles = sources inherited

            if
                not (List.contains kind (capabilityScopes capability))
                || (directRoles.IsEmpty && installationRoles.IsEmpty)
            then
                None
            else
                Some
                    {
                        Capability = capability
                        DirectRoles = directRoles
                        InstallationRoles = installationRoles
                        Effective = can principal view capability resource
                    })

    let correctionCapability =
        function
        | RecordGroup.Registration -> Capability.Register
        | RecordGroup.Decision -> Capability.Decide
        | RecordGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [ RecordGroup.Registration; RecordGroup.Decision ]
        |> List.map (fun group -> group, correctionCapability group)

    let visibility principal (view: AccessView) =
        if not (validActor principal view) then
            None
        elif can principal view Capability.ReadCase Scope.Installation then
            Some CaseVisibility.Every
        elif view.HasScopedCases then
            Some CaseVisibility.Scoped
        else
            None
