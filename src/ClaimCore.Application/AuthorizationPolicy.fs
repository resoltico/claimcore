namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain

module ActorAuthorization =
    // Application owns the one role-to-capability policy. Catalog owns spelling.
    let private matrix =
        [
            Role.Owner, [ Capability.ManageAuthority; Capability.ReadAuthority ]
            Role.CaseReader, [ Capability.ReadCase; Capability.ReadHistory ]
            Role.CaseRegistrar, [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
            Role.CaseHandler,
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.Register
                Capability.Decide
                Capability.Close
            ]
            Role.PaymentRecorder,
            [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
            Role.DataSteward, [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
            Role.Auditor,
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.ReadAuthority
            ]
        ]

    let roles = matrix |> List.map (fun (role, _) -> role)

    let capabilities role =
        matrix
        |> List.find (fun (candidate, _) -> candidate = role)
        |> fun (_, capabilities) -> capabilities

    let rolesFor capability =
        roles |> List.filter (fun role -> List.contains capability (capabilities role))

    /// Permanent role/principal invariant, separate from the evolving capability matrix.
    let humanOnlyRole =
        function
        | Role.Owner
        | Role.DataSteward -> true
        | _ -> false

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
        let humanOnly =
            capability = Capability.Steward || capability = Capability.ManageAuthority

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
        && (not humanOnly || PrincipalKey.isHuman principal)
        && (scoped
            |> Option.exists (
                List.exists (fun role -> List.contains capability (capabilities role))
            ))

    let private ordinaryActs =
        Map.ofList
            [
                (CaseActKind.Business BusinessActKind.Open), Capability.Register
                (CaseActKind.Business BusinessActKind.AmendRegistration), Capability.Register
                (CaseActKind.Business BusinessActKind.Decide), Capability.Decide
                (CaseActKind.Business BusinessActKind.WithdrawDecision), Capability.Decide
                (CaseActKind.Business BusinessActKind.RecordPayment), Capability.RecordPayment
                (CaseActKind.Business BusinessActKind.ClearPayment), Capability.RecordPayment
                (CaseActKind.Business BusinessActKind.Close), Capability.Close
                (CaseActKind.Business BusinessActKind.Reopen), Capability.Close
                (CaseActKind.Stewardship StewardshipActKind.Void), Capability.Steward
                (CaseActKind.Stewardship StewardshipActKind.Reinstate), Capability.Steward
                (CaseActKind.Stewardship StewardshipActKind.Erase), Capability.Steward
            ]

    let correctionCapability =
        function
        | RecordGroup.Registration -> Capability.Register
        | RecordGroup.Decision -> Capability.Decide
        | RecordGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | (CaseAct.Correct correction) ->
            [
                match correction.Registration with
                | RegistrationGroup.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | DecisionGroup.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision
                match correction.Payment with
                | PaymentGroup.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Payment
            ]
        | act -> [ ordinaryActs[CaseActs.kind act] ]

    let canAdvertise principal actor resource kind =
        if kind = (CaseActKind.Business BusinessActKind.Correct) then
            correctionCapabilities
            |> List.exists (fun capability -> can principal actor capability resource)
        else
            can principal actor ordinaryActs[kind] resource

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
