namespace ClaimCore.Application

open System

module ActorAuthorization =
    // One role-to-capability table; scope and human-only requirements are separate checks.
    let private matrix =
        [
            Role.Owner, [ Capability.ReadDefinition; Capability.ManageAuthority ]
            Role.CaseReader,
            [
                Capability.ReadDefinition
                Capability.ListCases
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ObserveOperation
            ]
            Role.CaseEditor,
            [
                Capability.ReadDefinition
                Capability.ListCases
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ReadHistoryFull
                Capability.ObserveOperation
                Capability.EditCase
            ]
            Role.DataSteward,
            [
                Capability.ReadDefinition
                Capability.ListCases
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ObserveOperation
                Capability.StewardCase
            ]
            Role.Auditor,
            [
                Capability.ReadDefinition
                Capability.ListCases
                Capability.ReadCase
                Capability.ReadHistorySummary
                Capability.ReadHistoryFull
                Capability.ObserveOperation
            ]
        ]

    let capabilities role =
        matrix |> List.find (fst >> (=) role) |> snd

    let rolesForCapability capability =
        matrix
        |> List.choose (fun (role, values) ->
            if List.contains capability values then Some role else None)

    let private covers resource scope =
        match resource, scope with
        | ResourceScope.Definition, _ -> true
        | _, GrantScope.Installation -> true
        | ResourceScope.Case target, GrantScope.Case granted ->
            target <> Guid.Empty && target = granted
        | _ -> false

    let validActor principal (actor: ActorAuthority) =
        actor.ActorId <> Guid.Empty && actor.Enabled && actor.Principal = principal

    let can principal actor capability resource =
        let humanOnly =
            capability = Capability.StewardCase || capability = Capability.ManageAuthority

        let validResource =
            match resource with
            | ResourceScope.Case id -> id <> Guid.Empty
            | _ -> true

        actor
        |> Option.exists (fun authority ->
            validResource
            && validActor principal authority
            && (not humanOnly || PrincipalKey.isHuman principal)
            && (authority.Grants
                |> List.exists (fun grant ->
                    covers resource grant.Scope
                    && List.contains capability (capabilities grant.Role))))

    let visibility principal (actor: ActorAuthority) =
        if not (validActor principal actor) then
            None
        else
            let scopes =
                actor.Grants
                |> List.filter (fun grant ->
                    List.contains Capability.ListCases (capabilities grant.Role))
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
