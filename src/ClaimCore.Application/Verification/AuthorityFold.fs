namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

/// Permanent authority interpretation uses only earlier ledger state, never projections or today's matrix.
module internal AuthorityFold =
    type private WireAuthorityAct = ClaimCore.Contract.Protocol.AuthorityAct

    let private ownerGrant: ActorGrant =
        {
            Role = Role.Owner
            Scope = GrantScope.Installation
        }

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains ownerGrant actor.Grants

    let executor (actors: Map<Guid, ActorAuthority>) (entry: StoredEntry) human ownerRequired =
        result {
            let! actor =
                Map.tryFind entry.ActorId actors
                |> known (Some entry.Sequence) VerificationFinding.ActorNotEnabled

            do! require (Some entry.Sequence) VerificationFinding.ActorNotEnabled actor.Enabled

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not human || PrincipalKey.isHuman actor.Principal)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.OwnerRequired
                    (not ownerRequired || owner actor)

            return ()
        }

    let private principal (entry: StoredEntry) (request: AuthorityRequestRecord) =
        PrincipalKey.fromStorage request.Target.Kind request.Target.Issuer request.Target.Subject
        |> Result.mapError (fun _ ->
            divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let private target (actors: Map<Guid, ActorAuthority>) (entry: StoredEntry) id principal =
        result {
            let! actor =
                Map.tryFind id actors
                |> known (Some entry.Sequence) VerificationFinding.AuthorityFold

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (actor.Principal = principal)

            return actor
        }

    let private register
        (actors: Map<Guid, ActorAuthority>)
        (entry: StoredEntry)
        id
        principal
        provision
        =
        result {
            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (id = entry.OperationId
                     && not (Map.containsKey id actors)
                     && (actors
                         |> Map.values
                         |> Seq.forall (fun actor -> actor.Principal <> principal)))

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (not provision
                     || (Map.isEmpty actors && entry.Sequence = 1L && entry.ActorId = id))

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not provision || PrincipalKey.isHuman principal)

            let actor =
                {
                    ActorId = id
                    Principal = principal
                    Enabled = true
                    Grants = if provision then [ ownerGrant ] else []
                }

            return Map.add id actor actors
        }

    let private grant
        installationId
        caseExists
        (entry: StoredEntry)
        (actor: ActorAuthority)
        role
        scope
        active
        =
        result {
            let! role =
                RoleTokens.tryParse role
                |> known (Some entry.Sequence) VerificationFinding.PayloadFormat

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not (ActorAuthorization.humanOnlyRole role)
                     || PrincipalKey.isHuman actor.Principal)

            let! scope =
                match scope with
                | GrantTarget.Installation -> Ok GrantScope.Installation
                | GrantTarget.CaseReference reference ->
                    let id = CaseReferenceId.encode installationId reference

                    require (Some entry.Sequence) VerificationFinding.AuthorityFold (caseExists id)
                    |> Result.map (fun () -> GrantScope.Case id)

            let grant: ActorGrant = { Role = role; Scope = scope }
            let grants = actor.Grants |> List.filter ((<>) grant)

            return
                { actor with
                    Grants = if active then grant :: grants else grants
                }
        }

    let private action
        installationId
        caseExists
        (actors: Map<Guid, ActorAuthority>)
        (entry: StoredEntry)
        id
        principal
        request
        =
        match request.Act with
        | RecordedAuthorityAct.ProvisionOwner -> register actors entry id principal true
        | RecordedAuthorityAct.Operation(WireAuthorityAct.RegisterActor) ->
            register actors entry id principal false
        | RecordedAuthorityAct.Operation(WireAuthorityAct.SetEnabled enabled) ->
            target actors entry id principal
            |> Result.map (fun actor -> Map.add id { actor with Enabled = enabled } actors)
        | RecordedAuthorityAct.Operation(WireAuthorityAct.SetGrant {
                                                                       Role = role
                                                                       Scope = scope
                                                                       Active = active
                                                                   }) ->
            target actors entry id principal
            |> Result.bind (fun actor ->
                grant installationId caseExists entry actor role scope active
                |> Result.map (fun actor -> Map.add id actor actors))

    let apply
        installationId
        caseExists
        (actors: Map<Guid, ActorAuthority>)
        (entry: StoredEntry)
        (request: AuthorityRequestRecord)
        =
        result {
            let! id, kind =
                match entry.Scope with
                | StoredScope.Authority(id, kind) -> Ok(id, kind)
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (request.OperationId = entry.OperationId
                     && RecordedAuthorityActs.kind request.Act = kind)

            let! principal = principal entry request

            do!
                if request.Act = RecordedAuthorityAct.ProvisionOwner then
                    Ok()
                else
                    executor actors entry true true

            let! next = action installationId caseExists actors entry id principal request

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (next |> Map.values |> Seq.exists owner)

            return next
        }
