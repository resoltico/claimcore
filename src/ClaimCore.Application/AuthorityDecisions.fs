namespace ClaimCore.Application

open ClaimCore.Catalog

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal AuthorityAction =
    | User of AuthorityAct
    | ProvisionOwner

module internal AuthorityDecisions =
    let private owner (view: AuthorityView) =
        view.Actor
        |> Option.exists (fun actor ->
            actor.Enabled
            && PrincipalKey.isHuman actor.Principal
            && List.contains Role.Owner view.Installation)

    let private grantProjection
        role
        scope
        active
        (actor: AuthorityView)
        (view: AuthorityLockedView)
        =
        let caseId =
            match scope, view.TargetCase with
            | GrantTarget.Installation, _ -> Ok None
            | GrantTarget.CaseReference _, Some record -> Ok(Some record.CaseId)
            | _ -> Error Rejection.ResourceUnavailable

        caseId
        |> Result.bind (fun id ->
            if
                ActorAuthorization.humanOnlyRole role
                && not (PrincipalKey.isHuman actor.Actor.Value.Principal)
            then
                Error Rejection.HumanPrincipalRequired
            elif
                not active
                && role = Role.Owner
                && id.IsNone
                && owner view.Target
                && view.EnabledHumanOwners <= 1
            then
                Error Rejection.LastOwner
            elif active then
                Ok(
                    actor.Actor.Value.ActorId,
                    AuthorityProjection.AddGrant(actor.Actor.Value.ActorId, role, id)
                )
            else
                Ok(
                    actor.Actor.Value.ActorId,
                    AuthorityProjection.RemoveGrant(actor.Actor.Value.ActorId, role, id)
                ))

    let private projection operationId target action (view: AuthorityLockedView) =
        match action, view.Target.Actor with
        | AuthorityAction.ProvisionOwner, _ when not (PrincipalKey.isHuman target) ->
            Error Rejection.HumanPrincipalRequired
        | AuthorityAction.ProvisionOwner, None when view.ActorCount = 0 ->
            Ok(operationId, AuthorityProjection.ProvisionOwner(operationId, target))
        | AuthorityAction.ProvisionOwner, _
        | AuthorityAction.User AuthorityAct.RegisterActor, Some _ ->
            Error Rejection.ActorAlreadyRegistered
        | AuthorityAction.User AuthorityAct.RegisterActor, None ->
            Ok(operationId, AuthorityProjection.RegisterActor(operationId, target))
        | _, None -> Error Rejection.ActorUnknown
        | AuthorityAction.User(AuthorityAct.SetEnabled enabled), Some actor ->
            if not enabled && owner view.Target && view.EnabledHumanOwners <= 1 then
                Error Rejection.LastOwner
            else
                Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, enabled))
        | AuthorityAction.User(AuthorityAct.SetGrant(role, scope, active)), Some _ ->
            grantProjection role scope active view.Target view

    let private targets installationId target action (view: AuthorityLockedView) =
        if
            view.Target.Actor
            |> Option.exists (fun actor -> actor.ActorId = Guid.Empty || actor.Principal <> target)
        then
            Error CoreFault.StoreIntegrityError
        else
            match action, view.TargetCase with
            | AuthorityAction.User(AuthorityAct.SetGrant(_, GrantTarget.CaseReference reference, _)),
              Some record ->
                CaseDecisions.current installationId record
                |> Result.bind (fun _ ->
                    if
                        CaseReference.key record.Snapshot.Reference = CaseReference.key reference
                    then
                        Ok()
                    else
                        Error CoreFault.StoreIntegrityError)
            | _ -> Ok()

    let private provisionIdentity installationId operationId bytes (view: AuthorityLockedView) =
        if view.ActorCount = 0 then
            if view.Head.Sequence = 0L && view.ExistingOperation.IsNone then
                IdentityDecision.Fresh
            else
                IdentityDecision.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> IdentityDecision.Refuse Rejection.ActorAlreadyRegistered
            | Some entry when entry.OperationId <> operationId ->
                IdentityDecision.Fail CoreFault.StoreIntegrityError
            | Some entry ->
                match LedgerEntries.validate installationId entry with
                | Error fault -> IdentityDecision.Fail fault
                | Ok validated ->
                    match LedgerEntries.identityBytes validated with
                    | Some accepted when accepted = bytes -> IdentityDecision.Replay validated
                    | _ -> IdentityDecision.Refuse Rejection.IdempotencyConflict

    let private append
        installationId
        principal
        operationId
        action
        bytes
        (view: AuthorityLockedView)
        targetId
        projection
        =
        let actorId =
            match action with
            | AuthorityAction.ProvisionOwner -> targetId
            | _ ->
                view.Actor.Actor
                |> Option.map _.ActorId
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof view) "Authorized actor is missing.")

        let record =
            AuthorityRecord.decode ServiceLimits.RequestByteLimit bytes
            |> Result.defaultWith (fun _ -> invalidArg (nameof bytes) "Invalid authority record.")

        let scope = StoredScope.Authority(targetId, RecordedAuthorityActs.kind record.Act)

        let entry =
            LedgerEntries.create
                installationId
                view.Head
                view.ObservedAt
                view.PayloadNonce
                actorId
                operationId
                scope
                bytes
                Array.empty

        LedgerEntries.validate installationId (LedgerEntries.stored entry)
        |> Result.bind (LedgerEntries.receipt principal view.Actor None)
        |> Result.map (fun receipt -> LedgerDecision.Append(entry, projection, receipt))
        |> Result.defaultWith LedgerDecision.Fail

    let private fresh
        installationId
        principal
        operationId
        target
        action
        bytes
        (view: AuthorityLockedView)
        =
        match targets installationId target action view with
        | Error fault -> LedgerDecision.Fail fault
        | Ok() ->
            match projection operationId target action view with
            | Error reason -> LedgerDecision.Refuse reason
            | Ok(targetId, projection) ->
                append installationId principal operationId action bytes view targetId projection

    let private decide
        installationId
        principal
        operationId
        target
        action
        bytes
        (view: AuthorityLockedView)
        =
        if
            action <> AuthorityAction.ProvisionOwner
            && not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ManageAuthority
                    ResourceScope.Installation
            )
        then
            LedgerDecision.Refuse Rejection.ResourceUnavailable
        else
            let identity =
                match action with
                | AuthorityAction.ProvisionOwner ->
                    provisionIdentity installationId operationId bytes view
                | _ ->
                    OperationIdentity.decide
                        installationId
                        principal
                        view.Actor
                        operationId
                        bytes
                        view.ExistingOperation

            match identity with
            | IdentityDecision.Replay entry ->
                LedgerEntries.receipt principal view.Actor None entry
                |> Result.map LedgerDecision.Replay
                |> Result.defaultWith LedgerDecision.Fail
            | IdentityDecision.Refuse reason -> LedgerDecision.Refuse reason
            | IdentityDecision.Fail fault -> LedgerDecision.Fail fault
            | IdentityDecision.Fresh when view.Head.Sequence = Revision.Maximum ->
                LedgerDecision.Refuse Rejection.LedgerExhausted
            | IdentityDecision.Fresh ->
                fresh installationId principal operationId target action bytes view

    let execute installationId principal (request: AuthorityRequest) (view: AuthorityLockedView) =
        let bytes = AuthorityRequests.toRecord request |> AuthorityRecord.encode

        decide
            installationId
            principal
            request.OperationId
            request.Target
            (AuthorityAction.User request.Act)
            bytes
            view

    /// Reachable only through the owner administration composition, never the actor facade.
    let provision installationId operationId target (view: AuthorityLockedView) =
        let bytes =
            AuthorityRequests.provisionRecord operationId target |> AuthorityRecord.encode

        decide installationId target operationId target AuthorityAction.ProvisionOwner bytes view
