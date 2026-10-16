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

    let rolesForCapability capability =
        roles |> List.filter (fun role -> List.contains capability (capabilities role))

    /// Permanent role/principal invariant, separate from the evolving capability matrix.
    let humanOnlyRole =
        function
        | Role.Owner
        | Role.DataSteward -> true
        | _ -> false

    let private covers resource scope =
        match resource, scope with
        | _, GrantScope.Installation -> true
        | ResourceScope.Case target, GrantScope.Case granted ->
            target <> Guid.Empty && target = granted
        | _ -> false

    let validActor principal (actor: ActorAuthority) =
        actor.ActorId <> Guid.Empty
        && actor.Enabled
        && actor.Principal = principal
        && (PrincipalKey.isHuman principal
            || actor.Grants |> List.forall (fun grant -> not (humanOnlyRole grant.Role)))

    let can principal actor capability resource =
        let humanOnly =
            capability = Capability.Steward || capability = Capability.ManageAuthority

        let validResource =
            match resource with
            | ResourceScope.Case id ->
                id <> Guid.Empty
                && capability <> Capability.ManageAuthority
                && capability <> Capability.ReadAuthority
            | ResourceScope.Installation -> true

        actor
        |> Option.exists (fun authority ->
            validResource
            && validActor principal authority
            && (not humanOnly || PrincipalKey.isHuman principal)
            && (authority.Grants
                |> List.exists (fun grant ->
                    covers resource grant.Scope
                    && List.contains capability (capabilities grant.Role))))

    let private ordinaryActs =
        Map.ofList
            [
                CaseActKind.Open, Capability.Register
                CaseActKind.AmendRegistration, Capability.Register
                CaseActKind.Decide, Capability.Decide
                CaseActKind.WithdrawDecision, Capability.Decide
                CaseActKind.RecordPayment, Capability.RecordPayment
                CaseActKind.ClearPayment, Capability.RecordPayment
                CaseActKind.Close, Capability.Close
                CaseActKind.Reopen, Capability.Close
                CaseActKind.Void, Capability.Steward
                CaseActKind.Reinstate, Capability.Steward
                CaseActKind.Erase, Capability.Steward
            ]

    let correctionGroups =
        [
            "registration", Capability.Register
            "decision", Capability.Decide
            "payment", Capability.RecordPayment
        ]

    let private correctionCapabilities = correctionGroups |> List.map snd

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | CaseAct.Business(BusinessCommand.CorrectCase correction) ->
            [
                match correction.Registration with
                | RegistrationCorrection.Keep -> ()
                | _ -> yield (correctionGroups |> List.find (fst >> (=) "registration") |> snd)
                match correction.Decision with
                | DecisionCorrection.Keep -> ()
                | _ -> yield (correctionGroups |> List.find (fst >> (=) "decision") |> snd)
                match correction.Payment with
                | PaymentCorrection.Keep -> ()
                | _ -> yield (correctionGroups |> List.find (fst >> (=) "payment") |> snd)
            ]
        | act -> [ ordinaryActs[CaseActs.kind act] ]

    let canAdvertise principal actor resource kind =
        if kind = CaseActKind.CorrectCase then
            correctionCapabilities
            |> List.exists (fun capability -> can principal actor capability resource)
        else
            can principal actor ordinaryActs[kind] resource

    let canAct principal actor resource act =
        canAdvertise principal actor resource (CaseActs.kind act)
        && (requiredCapabilities act
            |> List.forall (fun capability -> can principal actor capability resource))

    let visibility principal (actor: ActorAuthority) =
        if not (validActor principal actor) then
            None
        else
            let scopes =
                actor.Grants
                |> List.filter (fun grant ->
                    List.contains Capability.ReadCase (capabilities grant.Role))
                |> List.map _.Scope

            if List.contains GrantScope.Installation scopes then
                Some CaseVisibility.Every
            else
                let cases =
                    scopes
                    |> List.choose (function
                        | GrantScope.Case id when id <> Guid.Empty -> Some id
                        | _ -> None)
                    |> List.distinct

                if List.isEmpty cases then
                    None
                else
                    Some(CaseVisibility.Only cases)

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
