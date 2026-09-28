namespace ClaimCore.Application

open System

type internal ProjectedGrant =
    {
        Grant: ActorGrant
        Active: bool
        ChangedRevision: int64
    }

type internal ProjectedActor =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
        ChangedRevision: int64
        Grants: Map<ActorGrant, ProjectedGrant>
    }

type internal AuthorityProjection =
    {
        Revision: int64
        Actors: Map<Guid, ProjectedActor>
    }

/// Pure interpretation of the exact durable authority-event stream. This is not a login or
/// authorization side channel; admission still checks the live projection under the write lock.
module internal AuthorityHistory =
    let empty = { Revision = 0L; Actors = Map.empty }

    let private activeOwner (actor: ProjectedActor) =
        actor.Enabled
        && actor.Grants
           |> Map.exists (fun grant state ->
               state.Active && grant.Role = Role.Owner && grant.Scope = GrantScope.Installation)

    let private hasOwner projection =
        projection.Actors |> Map.exists (fun _ actor -> activeOwner actor)

    let private approverCanManage projection principalId =
        match projection.Actors |> Map.tryFind principalId with
        | None -> false
        | Some actor ->
            let authority =
                {
                    ActorId = actor.ActorId
                    Principal = actor.Principal
                    Enabled = actor.Enabled
                    GrantRevision = projection.Revision
                    Grants =
                        actor.Grants
                        |> Map.toList
                        |> List.choose (fun (grant, state) ->
                            if state.Active then Some grant else None)
                }

            ActorAuthorization.can
                actor.Principal
                authority
                EndpointAction.ManageGrants
                ResourceScope.Installation

    let private validApprover projection action =
        match action.ApproverActorId with
        | Some actorId when actorId <> Guid.Empty -> approverCanManage projection actorId
        | _ -> false

    let private addActor projection action principal initialGrant =
        if
            action.TargetActorId = Guid.Empty
            || projection.Actors.ContainsKey action.TargetActorId
            || projection.Actors |> Map.exists (fun _ actor -> actor.Principal = principal)
        then
            Error "AUTHORITY_ACTOR_CONFLICT"
        else
            let grants =
                match initialGrant with
                | None -> Map.empty
                | Some grant ->
                    Map.ofList
                        [
                            grant,
                            {
                                Grant = grant
                                Active = true
                                ChangedRevision = action.Revision
                            }
                        ]

            let actor =
                {
                    ActorId = action.TargetActorId
                    Principal = principal
                    Enabled = true
                    ChangedRevision = action.Revision
                    Grants = grants
                }

            Ok
                { projection with
                    Revision = action.Revision
                    Actors = projection.Actors.Add(action.TargetActorId, actor)
                }

    let private changeEnabled projection action enabled =
        match projection.Actors |> Map.tryFind action.TargetActorId with
        | None -> Error "AUTHORITY_ACTOR_MISSING"
        | Some actor when actor.Enabled = enabled -> Error "AUTHORITY_NOOP"
        | Some actor ->
            let updated =
                { actor with
                    Enabled = enabled
                    ChangedRevision = action.Revision
                }

            let result =
                { projection with
                    Revision = action.Revision
                    Actors = projection.Actors.Add(action.TargetActorId, updated)
                }

            if hasOwner result then
                Ok result
            else
                Error "AUTHORITY_LAST_OWNER"

    let private changeGrant projection action grant active =
        match projection.Actors |> Map.tryFind action.TargetActorId with
        | None -> Error "AUTHORITY_ACTOR_MISSING"
        | Some actor ->
            let existing = actor.Grants |> Map.tryFind grant

            if
                not (PrincipalKey.isHuman actor.Principal)
                && (grant.Role = Role.Owner || grant.Role = Role.DataSteward)
            then
                Error "AUTHORITY_SERVICE_PRIVILEGE"
            elif
                (existing |> Option.map _.Active) = Some active
                || (not active && existing.IsNone)
            then
                Error "AUTHORITY_NOOP"
            else
                let state =
                    {
                        Grant = grant
                        Active = active
                        ChangedRevision = action.Revision
                    }

                let updated =
                    { actor with
                        Grants = actor.Grants.Add(grant, state)
                    }

                let result =
                    { projection with
                        Revision = action.Revision
                        Actors = projection.Actors.Add(action.TargetActorId, updated)
                    }

                if hasOwner result then
                    Ok result
                else
                    Error "AUTHORITY_LAST_OWNER"

    let private applyInitial projection (action: ActorAuthorityAction) =
        match action.Principal, action.Grant, action.Enabled with
        | Some principal, Some grant, Some true when
            projection.Revision = 0L
            && action.ApproverActorId.IsNone
            && PrincipalKey.isHuman principal
            && grant =
                {
                    Role = Role.Owner
                    Scope = GrantScope.Installation
                }
            ->
            addActor projection action principal (Some grant)
        | _ -> Error "AUTHORITY_INITIAL_OWNER_INVALID"

    let private applyRegular projection (action: ActorAuthorityAction) =
        match action.ActionName, action.Principal, action.Grant, action.Enabled with
        | "REGISTER_ACTOR", Some principal, None, Some true ->
            addActor projection action principal None
        | "ENABLE_ACTOR", None, None, Some true -> changeEnabled projection action true
        | "DISABLE_ACTOR", None, None, Some false -> changeEnabled projection action false
        | "GRANT_ROLE", None, Some grant, Some true -> changeGrant projection action grant true
        | "REVOKE_ROLE", None, Some grant, Some false -> changeGrant projection action grant false
        | _ -> Error "AUTHORITY_ACTION_INVALID"

    let apply projection (action: ActorAuthorityAction) =
        if action.EventId = Guid.Empty || action.Revision <> projection.Revision + 1L then
            Error "AUTHORITY_REVISION_GAP"
        elif action.ActionName = "PROVISION_INITIAL_OWNER" then
            applyInitial projection action
        elif validApprover projection action then
            applyRegular projection action
        else
            Error "AUTHORITY_APPROVER_INVALID"

    let replay (actions: seq<ActorAuthorityAction>) =
        actions
        |> Seq.fold
            (fun state action -> state |> Result.bind (fun value -> apply value action))
            (Ok empty)
