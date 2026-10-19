namespace ClaimCore.Application

open System
open ClaimCore.Domain

module ActorAuthorization =
    // The one role catalogue owns tokens and capabilities together.
    let private matrix =
        [
            Role.Owner, "OWNER", [ Capability.ManageAuthority; Capability.ReadAuthority ]
            Role.CaseReader, "CASE_READER", [ Capability.ReadCase; Capability.ReadHistory ]
            Role.CaseRegistrar,
            "CASE_REGISTRAR",
            [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
            Role.CaseHandler,
            "CASE_HANDLER",
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.Register
                Capability.Decide
                Capability.Close
            ]
            Role.PaymentRecorder,
            "PAYMENT_RECORDER",
            [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
            Role.DataSteward,
            "DATA_STEWARD",
            [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
            Role.Auditor,
            "AUDITOR",
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.ReadAuthority
            ]
        ]

    let roles = matrix |> List.map (fun (role, _, _) -> role)

    let roleToken role =
        matrix
        |> List.find (fun (candidate, _, _) -> candidate = role)
        |> fun (_, token, _) -> token

    let tryRole value =
        roles |> List.tryFind (roleToken >> (=) value)

    let capabilities role =
        matrix
        |> List.find (fun (candidate, _, _) -> candidate = role)
        |> fun (_, _, capabilities) -> capabilities

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
        | CorrectionGroup.Registration -> Capability.Register
        | CorrectionGroup.Decision -> Capability.Decide
        | CorrectionGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [
            CorrectionGroup.Registration
            CorrectionGroup.Decision
            CorrectionGroup.Payment
        ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | CaseAct.Business(BusinessCommand.CorrectCase correction) ->
            [
                match correction.Registration with
                | RegistrationCorrection.Keep -> ()
                | _ -> yield correctionCapability CorrectionGroup.Registration
                match correction.Decision with
                | DecisionCorrection.Keep -> ()
                | _ -> yield correctionCapability CorrectionGroup.Decision
                match correction.Payment with
                | PaymentCorrection.Keep -> ()
                | _ -> yield correctionCapability CorrectionGroup.Payment
            ]
        | act -> [ ordinaryActs[CaseActs.kind act] ]

    let canAdvertise principal actor resource kind =
        if kind = (CaseActKind.Business BusinessActKind.CorrectCase) then
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

module Roles =
    let all = ActorAuthorization.roles
    let token role = ActorAuthorization.roleToken role
    let tryParse value = ActorAuthorization.tryRole value
    let requiresHuman role = ActorAuthorization.humanOnlyRole role

module Capabilities =
    let all = Roles.all |> List.collect ActorAuthorization.capabilities |> List.distinct

    let token =
        function
        | Capability.ReadCase -> "READ_CASE"
        | Capability.ReadHistory -> "READ_HISTORY"
        | Capability.ReadHistoryFull -> "READ_HISTORY_FULL"
        | Capability.Register -> "REGISTER"
        | Capability.Decide -> "DECIDE"
        | Capability.RecordPayment -> "RECORD_PAYMENT"
        | Capability.Close -> "CLOSE"
        | Capability.Steward -> "STEWARD"
        | Capability.ManageAuthority -> "MANAGE_AUTHORITY"
        | Capability.ReadAuthority -> "READ_AUTHORITY"
