namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal AuthorityAction =
    | User of AdmittedAuthorityAct
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
        subject
        active
        (actor: AuthorityView)
        (view: AuthorityReadView)
        =
        let caseId =
            match subject, view.TargetCase with
            | GrantTarget.Installation, _ -> Ok None
            | GrantTarget.CaseReference _, Some record -> Ok(Some record.CaseId)
            | _ -> Error Refusal.ResourceUnavailable

        caseId
        |> Result.bind (fun id ->
            if
                ActorAuthorization.humanOnlyRole role
                && not (PrincipalKey.isHuman actor.Actor.Value.Principal)
            then
                Error Refusal.HumanPrincipalRequired
            elif
                not active
                && role = Role.Owner
                && id.IsNone
                && owner view.Target
                && view.EnabledHumanOwners <= 1
            then
                Error Refusal.LastOwner
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

    let private projection operationId target action (view: AuthorityReadView) =
        match action, view.Target.Actor with
        | AuthorityAction.ProvisionOwner, _ when not (PrincipalKey.isHuman target) ->
            Error Refusal.HumanPrincipalRequired
        | AuthorityAction.ProvisionOwner, None when view.ActorCount = 0 ->
            Ok(operationId, AuthorityProjection.ProvisionOwner(operationId, target))
        | AuthorityAction.ProvisionOwner, _
        | AuthorityAction.User AdmittedAuthorityAct.Register, Some _ ->
            Error Refusal.ActorAlreadyRegistered
        | AuthorityAction.User AdmittedAuthorityAct.Register, None ->
            Ok(operationId, AuthorityProjection.Register(operationId, target))
        | _, None -> Error Refusal.ActorUnknown
        | AuthorityAction.User AdmittedAuthorityAct.Disable, Some _ when
            owner view.Target && view.EnabledHumanOwners <= 1
            ->
            Error Refusal.LastOwner
        | AuthorityAction.User AdmittedAuthorityAct.Enable, Some actor ->
            Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, true))
        | AuthorityAction.User AdmittedAuthorityAct.Disable, Some actor ->
            Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, false))
        | AuthorityAction.User(AdmittedAuthorityAct.Grant(role, subject)), Some _ ->
            grantProjection role subject true view.Target view
        | AuthorityAction.User(AdmittedAuthorityAct.Revoke(role, subject)), Some _ ->
            grantProjection role subject false view.Target view

    let private targets installationId target action (view: AuthorityReadView) =
        if
            view.Target.Actor
            |> Option.exists (fun actor -> actor.ActorId = Guid.Empty || actor.Principal <> target)
        then
            Error CoreFault.StoreIntegrityError
        else
            match action, view.TargetCase with
            | AuthorityAction.User(AdmittedAuthorityAct.Grant(_, GrantTarget.CaseReference reference)),
              Some record
            | AuthorityAction.User(AdmittedAuthorityAct.Revoke(_,
                                                               GrantTarget.CaseReference reference)),
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

    let private admission installationId principal operationId bytes (view: AuthorityReadView) =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ManageAuthority
                    ResourceScope.Installation
            )
        then
            IdentityDecision.Refuse Refusal.ResourceUnavailable
        else
            OperationIdentity.decide
                installationId
                principal
                view.Actor
                operationId
                bytes
                view.ExistingOperation


    let private propose installationId operationId target action view =
        match targets installationId target action view with
        | Error fault -> LedgerReviewDecision.Fail fault
        | Ok() ->
            match projection operationId target action view with
            | Error refusal -> LedgerReviewDecision.Refuse refusal
            | Ok(targetId, projection) -> LedgerReviewDecision.Proposed(targetId, projection)

    let private provisionIdentity installationId operationId bytes (view: AuthorityLockedView) =
        if view.Read.ActorCount = 0 then
            if view.Head.Sequence = 0L && view.Read.ExistingOperation.IsNone then
                IdentityDecision.Fresh
            else
                IdentityDecision.Fail CoreFault.StoreIntegrityError
        else
            match view.Read.ExistingOperation with
            | None -> IdentityDecision.Refuse Refusal.ActorAlreadyRegistered
            | Some entry when entry.OperationId <> operationId ->
                IdentityDecision.Fail CoreFault.StoreIntegrityError
            | Some entry ->
                match LedgerEntries.validate installationId entry with
                | Error fault -> IdentityDecision.Fail fault
                | Ok validated ->
                    match LedgerEntries.identityBytes validated with
                    | Some accepted when accepted = bytes -> IdentityDecision.Replay validated
                    | _ -> IdentityDecision.Refuse Refusal.IdempotencyConflict

    let private project
        action
        (record: AuthorityRequestRecord)
        (view: AuthorityReadView)
        (targetId, projection)
        =
        {
            ObservedAt = view.ObservedAt
            ActorId =
                match action with
                | AuthorityAction.ProvisionOwner -> targetId
                | AuthorityAction.User _ -> LedgerEnvelope.actorId view.Actor
            Subject = StoredSubject.Authority(targetId, RecordedAuthorityActs.kind record.Act)
            Snapshot = Array.empty
            Current = None
            Projection = projection
        }

    let private subject installationId principal target action record admit =
        {
            Encode = fun () -> AuthorityRecord.encode record
            Canonical = fun () -> AuthorityRequests.canonical record |> AuthorityRecord.encode
            Admit = admit
            Propose = propose installationId record.OperationId target action
            Project = project action record
            Receipt =
                fun (view: AuthorityReadView) _ entry ->
                    LedgerEntries.receipt principal view.Actor None entry
        }

    let private userScope installationId principal (request: AdmittedAuthorityRequest) =
        let record = AuthorityRequests.toRecord request

        subject
            installationId
            principal
            request.Target
            (AuthorityAction.User request.Act)
            record
            (admission installationId principal request.OperationId)

    let review installationId principal request (view: AuthorityReadView) =
        LedgerEnvelope.review (userScope installationId principal request) view

    let execute
        installationId
        principal
        (request: AdmittedAuthorityRequest)
        (view: AuthorityLockedView)
        =
        LedgerEnvelope.execute
            installationId
            request.OperationId
            (userScope installationId principal request)
            view.Head
            view.IsFenced
            view.PayloadNonce
            view.Read

    /// Owner provisioning shares sequencing while keeping its genesis/identity rule.
    let provision installationId operationId target (view: AuthorityLockedView) =
        let record = AuthorityRequests.provisionRecord operationId target

        let admit bytes _ =
            provisionIdentity installationId operationId bytes view

        let subject =
            subject installationId target target AuthorityAction.ProvisionOwner record admit

        LedgerEnvelope.execute
            installationId
            operationId
            subject
            view.Head
            view.IsFenced
            view.PayloadNonce
            view.Read
