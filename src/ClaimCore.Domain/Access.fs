namespace ClaimCore.Domain

/// Potential sources remain visible for disabled actors; Effective reflects current actor validity.
type CapabilityAccess =
    {
        Capability: Capability
        DirectRoles: Role list
        InstallationRoles: Role list
        Effective: bool
    }

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
            ]

    let roles = Role.all

    let rolesFor capability =
        roles |> List.filter (fun role -> List.contains capability (policy role))

    /// Scope meaning belongs to capability policy, independently of principal-kind admission.
    let capabilityScopes =
        function
        | Capability.ManageAccess
        | Capability.ReadAccess -> [ ScopeKind.Installation ]
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
        | Capability.ReadAccess -> false

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

    let private correctionCapabilities = correctionGroups |> List.map snd

    let private capabilities kind =
        match (CaseActKind.describe kind).Capabilities with
        | ActCapabilities.Required values -> values
        | ActCapabilities.CorrectionGroups -> correctionCapabilities

    /// Only asserted correction groups require their business capabilities.
    let requiredCapabilities =
        function
        | (CaseAct.Correct correction) ->
            [
                match correction.Registration with
                | Amendment.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | Change.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision

            ]
        | act -> capabilities (CaseAct.kind act)

    let canAdvertise principal actor resource kind =
        match (CaseActKind.describe kind).Capabilities with
        | ActCapabilities.Required values ->
            values
            |> List.forall (fun capability -> can principal actor capability resource)
        | ActCapabilities.CorrectionGroups ->
            correctionCapabilities
            |> List.exists (fun capability -> can principal actor capability resource)

    /// An empty correction still needs an offered fact capability; concrete groups keep their own requirements.
    let missingCapabilities principal actor resource act =
        let required =
            match requiredCapabilities act with
            | [] when CaseAct.kind act = CaseActKind.Correct -> correctionCapabilities
            | values -> values

        required
        |> List.filter (fun capability -> not (can principal actor capability resource))

    let canAct principal actor resource act =
        canAdvertise principal actor resource (CaseAct.kind act)
        && (requiredCapabilities act
            |> List.forall (fun capability -> can principal actor capability resource))

    let visibility principal (view: AccessView) =
        if not (validActor principal view) then
            None
        elif can principal view Capability.ReadCase Scope.Installation then
            Some CaseVisibility.Every
        elif view.HasScopedCases then
            Some CaseVisibility.Scoped
        else
            None
