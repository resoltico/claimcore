namespace ClaimCore.Application

open System
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal AuthorityAction =
    | Register
    | ProvisionOwner
    | Grant of Role * GrantTarget * bool
    | Enable of bool

module internal AuthorityDecisions =
    let record eventId target action =
        let kind, issuer, subject = PrincipalKey.storageParts target

        let recorded =
            match action with
            | AuthorityAction.Register -> RecordedAuthorityAction.RegisterActor
            | AuthorityAction.ProvisionOwner -> RecordedAuthorityAction.ProvisionOwner
            | AuthorityAction.Enable enabled -> RecordedAuthorityAction.SetEnabled enabled
            | AuthorityAction.Grant(role, scope, active) ->
                RecordedAuthorityAction.SetGrant(
                    Roles.token role,
                    (match scope with
                     | GrantTarget.Installation -> RecordedGrantScope.Installation
                     | GrantTarget.CaseReference value -> RecordedGrantScope.CaseReference value),
                    active
                )

        {
            EventId = eventId
            Target =
                {
                    Kind = kind
                    Issuer = issuer
                    Subject = subject
                }
            Action = recorded
        }

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains
            {
                Role = Role.Owner
                Scope = GrantScope.Installation
            }
            actor.Grants

    let private projection eventId target action (view: AuthorityLockedView) =
        match action, view.Target with
        | AuthorityAction.Register, Some _ -> Error Rejection.ActorAlreadyRegistered
        | AuthorityAction.Register, None ->
            Ok(eventId, AuthorityProjection.RegisterActor(eventId, target))
        | AuthorityAction.ProvisionOwner, _ when not (PrincipalKey.isHuman target) ->
            Error Rejection.HumanPrincipalRequired
        | AuthorityAction.ProvisionOwner, None when view.ActorCount = 0 ->
            Ok(eventId, AuthorityProjection.ProvisionOwner(eventId, target))
        | AuthorityAction.ProvisionOwner, _ -> Error Rejection.ActorAlreadyRegistered
        | _, None -> Error Rejection.ActorUnknown
        | AuthorityAction.Enable enabled, Some actor ->
            if not enabled && owner actor && view.EnabledHumanOwners <= 1 then
                Error Rejection.LastOwner
            else
                Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, enabled))
        | AuthorityAction.Grant(role, scope, active), Some actor ->
            let caseId =
                match scope, view.TargetCase with
                | GrantTarget.Installation, _ -> Ok None
                | GrantTarget.CaseReference _, Some case -> Ok(Some case.CaseId)
                | _ -> Error Rejection.ResourceUnavailable

            caseId
            |> Result.bind (fun id ->
                if
                    not active
                    && role = Role.Owner
                    && id.IsNone
                    && owner actor
                    && view.EnabledHumanOwners <= 1
                then
                    Error Rejection.LastOwner
                elif active then
                    Ok(actor.ActorId, AuthorityProjection.AddGrant(actor.ActorId, role, id))
                else
                    Ok(actor.ActorId, AuthorityProjection.RemoveGrant(actor.ActorId, role, id)))

    let private identity principal action bytes (view: AuthorityLockedView) =
        if action = AuthorityAction.ProvisionOwner && view.ActorCount = 0 then
            if view.Head.Sequence = 0L then
                IdentityDecision.Fresh
            else
                IdentityDecision.Fail CoreFault.StoreIntegrityError
        elif action = AuthorityAction.ProvisionOwner then
            match view.ExistingOperation with
            | Some entry ->
                match LedgerEntries.payload entry with
                | Ok payload when payload.Request = bytes -> IdentityDecision.Replay entry
                | Ok _ -> IdentityDecision.Refuse Rejection.IdempotencyConflict
                | Error fault -> IdentityDecision.Fail fault
            | None -> IdentityDecision.Refuse Rejection.ActorAlreadyRegistered
        else
            OperationIdentity.decide principal view.Actor bytes view.ExistingOperation

    let decide installationId principal eventId target action (view: AuthorityLockedView) =
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
            AuthorityDecision.Refuse Rejection.ResourceUnavailable
        else
            let bytes = record eventId target action |> AuthorityRecord.encode

            let identity = identity principal action bytes view

            match identity with
            | IdentityDecision.Replay entry -> AuthorityDecision.Replay entry
            | IdentityDecision.Refuse reason -> AuthorityDecision.Refuse reason
            | IdentityDecision.Fail fault -> AuthorityDecision.Fail fault
            | IdentityDecision.Fresh ->
                match projection eventId target action view with
                | Error reason -> AuthorityDecision.Refuse reason
                | Ok(targetId, projection) ->
                    let actorId =
                        if action = AuthorityAction.ProvisionOwner then
                            targetId
                        else
                            view.Actor
                            |> Option.map _.ActorId
                            |> Option.defaultWith (fun () -> invalidOp "Authorized actor missing.")

                    AuthorityDecision.Append(
                        LedgerEntries.create
                            installationId
                            view.Head
                            view.ObservedAt
                            view.PayloadNonce
                            actorId
                            eventId
                            (StoredScope.Authority targetId)
                            bytes
                            [||],
                        projection
                    )
