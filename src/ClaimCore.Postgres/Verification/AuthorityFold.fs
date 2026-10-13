namespace ClaimCore.Postgres

open System
open ClaimCore.Application
open ClaimCore.RecordFormat

module internal AuthorityFold =
    let private grant cases (actor: ActorAuthority) role scope active =
        let role =
            Roles.tryParse role
            |> Option.defaultWith (fun () -> invalidOp "Invalid authority role.")

        let scope =
            match scope with
            | RecordedGrantScope.Installation -> GrantScope.Installation
            | RecordedGrantScope.CaseReference reference ->
                cases
                |> List.tryFind (fun case -> case.Reference = reference)
                |> Option.map (_.CaseId >> GrantScope.Case)
                |> Option.defaultWith (fun () -> invalidOp "Authority case is absent.")

        let grant = { Role = role; Scope = scope }
        let grants = actor.Grants |> List.filter ((<>) grant)
        let grants = if active then grant :: grants else grants
        { actor with Grants = grants }

    let private register (actors: Map<Guid, ActorAuthority>) (entry: StoredEntry) principal action =
        let grants =
            if action = RecordedAuthorityAction.ProvisionOwner then
                if
                    not actors.IsEmpty
                    || not (PrincipalKey.isHuman principal)
                    || entry.ActorId <> entry.OperationId
                then
                    invalidOp "Invalid owner provisioning entry."

                [
                    {
                        Role = Role.Owner
                        Scope = GrantScope.Installation
                    }
                ]
            else
                []

        Map.add
            entry.OperationId
            {
                ActorId = entry.OperationId
                Principal = principal
                Enabled = true
                Grants = grants
            }
            actors

    let apply
        (cases: StoredCase list)
        (actors: Map<Guid, ActorAuthority>)
        (entry: StoredEntry)
        (payload: LedgerPayload)
        =
        let request =
            AuthorityRecord.decode 65536 payload.Request
            |> Result.defaultWith (fun _ -> invalidOp "Invalid authority record.")

        if
            AuthorityRecord.encode request <> payload.Request
            || request.EventId <> entry.OperationId
            || payload.Snapshot.Length <> 0
        then
            invalidOp "Authority payload identity differs."

        let principal =
            PrincipalKey.fromStorage
                request.Target.Kind
                request.Target.Issuer
                request.Target.Subject
            |> Result.defaultWith (fun _ -> invalidOp "Invalid authority principal.")

        let target =
            actors
            |> Map.toSeq
            |> Seq.tryFind (fun (_, actor) -> actor.Principal = principal)
            |> Option.map snd

        match request.Action, target with
        | RecordedAuthorityAction.RegisterActor, None
        | RecordedAuthorityAction.ProvisionOwner, None ->
            register actors entry principal request.Action
        | RecordedAuthorityAction.SetEnabled enabled, Some actor ->
            Map.add actor.ActorId { actor with Enabled = enabled } actors
        | RecordedAuthorityAction.SetGrant(role, scope, active), Some actor ->
            Map.add actor.ActorId (grant cases actor role scope active) actors
        | _ -> invalidOp "Authority history cannot be folded."
