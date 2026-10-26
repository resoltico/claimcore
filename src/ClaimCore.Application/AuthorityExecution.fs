namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal AuthorityAction =
    | User of AdmittedAuthorityAct
    | ProvisionOwner

module internal AuthorityExecution =
    let private owner (view: AuthorityView) =
        view.Actor
        |> Option.exists (fun actor ->
            actor.Enabled
            && PrincipalKey.isHuman actor.Principal
            && List.contains Role.Owner view.Installation)

    let private grantProjection role subject active (actor: AuthorityView) (view: AuthorityFacts) =
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

    let private projection operationId target action (view: AuthorityFacts) =
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

    let private targets installationId target action (view: AuthorityFacts) =
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
                CaseExecution.current installationId record
                |> Result.bind (fun _ ->
                    if
                        CaseReference.identity record.Snapshot.Reference =
                            CaseReference.key reference
                    then
                        Ok()
                    else
                        Error CoreFault.StoreIntegrityError)
            | _ -> Ok()

    let private authorizeIdentity
        installationId
        principal
        operationId
        bytes
        (view: AuthorityFacts)
        =
        if
            not (
                ActorAuthorization.can
                    principal
                    view.Actor
                    Capability.ManageAuthority
                    ResourceScope.Installation
            )
        then
            Verdict.Refuse Refusal.ResourceUnavailable
        else
            OperationIdentity.decide
                installationId
                principal
                view.Actor
                operationId
                bytes
                view.ExistingOperation
                (LedgerEntries.receipt principal view.Actor None)


    let private propose installationId operationId target action view =
        match targets installationId target action view with
        | Error fault -> Verdict.Fail fault
        | Ok() ->
            match projection operationId target action view with
            | Error refusal -> Verdict.Refuse refusal
            | Ok(targetId, projection) -> Verdict.Proceed(targetId, projection)

    let private provisionIdentity
        installationId
        operationId
        principal
        bytes
        (head: LedgerHead)
        (view: AuthorityFacts)
        =
        if view.ActorCount = 0 then
            if head.Sequence = 0L && view.ExistingOperation.IsNone then
                Verdict.Proceed()
            else
                Verdict.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> Verdict.Refuse Refusal.ActorAlreadyRegistered
            | Some entry when entry.OperationId <> operationId ->
                Verdict.Fail CoreFault.StoreIntegrityError
            | Some entry ->
                match LedgerEntries.validate installationId entry with
                | Error fault -> Verdict.Fail fault
                | Ok validated ->
                    match LedgerEntries.identityBytes validated with
                    | Some accepted when accepted = bytes ->
                        LedgerEntries.receipt principal view.Actor None validated
                        |> Result.map Verdict.Replay
                        |> Result.defaultWith Verdict.Fail
                    | _ -> Verdict.Refuse Refusal.IdempotencyConflict

    let private project
        action
        (record: AuthorityRequestRecord)
        (view: AuthorityFacts)
        (targetId, projection)
        =
        {
            ObservedAt = view.ObservedAt
            ActorId =
                match action with
                | AuthorityAction.ProvisionOwner -> targetId
                | AuthorityAction.User _ -> LedgerEnvelope.actorId view.Actor
            Subject = EntryTarget.Authority(targetId, RecordedAuthorityActs.kind record.Act)
            Snapshot = Array.empty
            Current = None
            Projection = projection
        }

    let review installationId principal (request: AdmittedAuthorityRequest) (view: AuthorityFacts) =
        let record = AuthorityRequests.toRecord request
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        authorizeIdentity installationId principal request.OperationId bytes view
        |> Verdict.bind (fun () ->
            propose
                installationId
                request.OperationId
                request.Target
                (AuthorityAction.User request.Act)
                view)

    let private executeRecord
        installationId
        principal
        operationId
        target
        action
        record
        admit
        head
        fenced
        nonce
        (view: AuthorityFacts)
        =
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        admit bytes
        |> Verdict.bind (fun () -> LedgerEnvelope.guard fenced)
        |> Verdict.bind (fun () -> propose installationId operationId target action view)
        |> Verdict.bind (fun proposed ->
            let plan = project action record view proposed

            LedgerEnvelope.accept
                installationId
                principal
                view.Actor
                operationId
                head
                nonce
                plan
                (AuthorityRecord.encode record))

    let execute
        installationId
        principal
        (request: AdmittedAuthorityRequest)
        head
        fenced
        nonce
        (view: AuthorityFacts)
        =
        executeRecord
            installationId
            principal
            request.OperationId
            request.Target
            (AuthorityAction.User request.Act)
            (AuthorityRequests.toRecord request)
            (fun bytes -> authorizeIdentity installationId principal request.OperationId bytes view)
            head
            fenced
            nonce
            view

    let provision installationId operationId target head fenced nonce (view: AuthorityFacts) =
        executeRecord
            installationId
            target
            operationId
            target
            AuthorityAction.ProvisionOwner
            (AuthorityRequests.provisionRecord operationId target)
            (fun bytes -> provisionIdentity installationId operationId target bytes head view)
            head
            fenced
            nonce
            view

    let run (store: IStore) installationId principal (request: AdmittedAuthorityRequest) token =
        store.Write(
            (fun tx ->
                task {
                    let! view =
                        ExecutionFacts.target
                            tx
                            installationId
                            principal
                            request.OperationId
                            request.Target
                            (AuthorityRequests.targetCase request)

                    let! fenced = ExecutionFacts.fenced tx view.Actor request.OperationId

                    return!
                        execute installationId principal request tx.Head fenced tx.Nonce view
                        |> ExecutionFacts.persist tx tx.ProjectAuthority
                }),
            token
        )

    let provisionStore (store: IStore) installationId operationId target token =
        store.Write(
            (fun tx ->
                task {
                    let! view =
                        ExecutionFacts.target tx installationId target operationId target None

                    let! fenced = ExecutionFacts.fenced tx view.Actor operationId

                    return!
                        provision installationId operationId target tx.Head fenced tx.Nonce view
                        |> ExecutionFacts.persist tx tx.ProjectAuthority
                }),
            token
        )
