namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain

module ActorAuthorization =
    // Application owns the one role-to-capability policy. Catalog owns spelling.
    let capabilities =
        function
        | Role.Owner -> [ Capability.ManageAuthority; Capability.ReadAuthority ]
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
            ]
        | Role.PaymentRecorder ->
            [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
        | Role.DataSteward -> [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
        | Role.Auditor ->
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.ReadAuthority
            ]

    let roles = RoleTokens.all

    let rolesFor capability =
        roles |> List.filter (fun role -> List.contains capability (capabilities role))

    /// Principal restrictions follow the capabilities a role grants.
    let humanOnlyCapability =
        function
        | Capability.Steward
        | Capability.ManageAuthority -> true
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ReadAuthority -> false

    let humanOnlyRole role =
        capabilities role |> List.exists humanOnlyCapability

    let invalidRoles principal =
        if PrincipalKey.isHuman principal then
            []
        else
            roles |> List.filter humanOnlyRole

    let readRoles principal =
        rolesFor Capability.ReadCase
        |> List.filter (fun role -> PrincipalKey.isHuman principal || not (humanOnlyRole role))

    let validActor principal (view: AuthorityView) =
        let loaded =
            view.Installation @ (view.Cases |> Map.values |> Seq.collect id |> Seq.toList)

        view.Actor
        |> Option.exists (fun actor ->
            actor.ActorId <> Guid.Empty
            && actor.Enabled
            && actor.Principal = principal
            && (PrincipalKey.isHuman principal || loaded |> List.forall (humanOnlyRole >> not)))

    let can principal (view: AuthorityView) capability resource =
        let scoped =
            match resource with
            | ResourceScope.Installation -> Some view.Installation
            | ResourceScope.Case id when
                id <> Guid.Empty
                && capability <> Capability.ManageAuthority
                && capability <> Capability.ReadAuthority
                ->
                Some(view.Installation @ (view.Cases |> Map.tryFind id |> Option.defaultValue []))
            | _ -> None

        validActor principal view
        && (not (humanOnlyCapability capability) || PrincipalKey.isHuman principal)
        && (scoped
            |> Option.exists (
                List.exists (fun role -> List.contains capability (capabilities role))
            ))

    let correctionCapability =
        function
        | RecordGroup.Registration -> Capability.Register
        | RecordGroup.Decision -> Capability.Decide
        | RecordGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    let private ordinaryActs =
        function
        | CaseActKind.Open
        | CaseActKind.AmendRegistration -> [ Capability.Register ]
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision -> [ Capability.Decide ]
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment -> [ Capability.RecordPayment ]
        | CaseActKind.Close
        | CaseActKind.Reopen -> [ Capability.Close ]
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> [ Capability.Steward ]
        | CaseActKind.Correct -> correctionCapabilities

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | (CaseAct.Correct correction) ->
            [
                match correction.Registration with
                | RegistrationChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | DecisionChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision
                match correction.Payment with
                | PaymentChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Payment
            ]
        | act -> ordinaryActs (CaseActs.kind act)

    let canAdvertise principal actor resource kind =
        ordinaryActs kind
        |> List.exists (fun capability -> can principal actor capability resource)

    let canAct principal actor resource act =
        canAdvertise principal actor resource (CaseActs.kind act)
        && (requiredCapabilities act
            |> List.forall (fun capability -> can principal actor capability resource))

    let visibility principal (view: AuthorityView) =
        if not (validActor principal view) then
            None
        elif can principal view Capability.ReadCase ResourceScope.Installation then
            Some CaseVisibility.Every
        elif view.HasAssignedCases then
            Some CaseVisibility.Assigned
        else
            None
