namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal AuthorityAction =
    | User of AdmittedAuthorityAct
    | ProvisionOwner

[<RequireQualifiedAccess; NoComparison>]
type internal AuthorityReviewDecision =
    | Proposed of targetActorId: Guid * AuthorityProjection
    | Replay of OperationReceipt
    | Refuse of Refusal
    | Fail of CoreFault

module internal AuthorityDecisions =
    let private owner (view: AuthorityView) =
        view.Actor
        |> Option.exists (fun actor ->
            actor.Enabled
            && PrincipalKey.isHuman actor.Principal
            && List.contains Role.Owner view.Installation)

    let private grantProjection role scope active (actor: AuthorityView) (view: AuthorityReadView) =
        let caseId =
            match scope, view.TargetCase with
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
        | AuthorityAction.User AdmittedAuthorityAct.RegisterActor, Some _ ->
            Error Refusal.ActorAlreadyRegistered
        | AuthorityAction.User AdmittedAuthorityAct.RegisterActor, None ->
            Ok(operationId, AuthorityProjection.RegisterActor(operationId, target))
        | _, None -> Error Refusal.ActorUnknown
        | AuthorityAction.User(AdmittedAuthorityAct.SetEnabled enabled), Some actor ->
            if not enabled && owner view.Target && view.EnabledHumanOwners <= 1 then
                Error Refusal.LastOwner
            else
                Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, enabled))
        | AuthorityAction.User(AdmittedAuthorityAct.SetGrant(role, scope, active)), Some _ ->
            grantProjection role scope active view.Target view

    let private targets installationId target action (view: AuthorityReadView) =
        if
            view.Target.Actor
            |> Option.exists (fun actor -> actor.ActorId = Guid.Empty || actor.Principal <> target)
        then
            Error CoreFault.StoreIntegrityError
        else
            match action, view.TargetCase with
            | AuthorityAction.User(AdmittedAuthorityAct.SetGrant(_,
                                                                 GrantTarget.CaseReference reference,
                                                                 _)),
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

    let stage
        installationId
        principal
        (request: AdmittedAuthorityRequest)
        (view: AuthorityReadView)
        =
        let bytes = AuthorityRequests.toRecord request |> AuthorityRecord.encode

        match admission installationId principal request.OperationId bytes view with
        | IdentityDecision.Refuse refusal ->
            RecoveryAdmission.Terminal(ExecuteResult.Rejected refusal)
        | IdentityDecision.Fail fault -> RecoveryAdmission.Terminal(ExecuteResult.Failed fault)
        | IdentityDecision.Replay entry ->
            LedgerEntries.receipt principal view.Actor None entry
            |> Result.map ExecuteResult.AlreadyAccepted
            |> Result.defaultWith ExecuteResult.Failed
            |> RecoveryAdmission.Terminal
        | IdentityDecision.Fresh when
            request.Act <> AdmittedAuthorityAct.RegisterActor && view.Target.Actor.IsNone
            ->
            RecoveryAdmission.Terminal(ExecuteResult.Rejected Refusal.ActorUnknown)
        | IdentityDecision.Fresh ->
            match view.TargetCase with
            | Some {
                       Snapshot = { View = CaseView.Erased }
                   } -> RecoveryAdmission.Terminal(ExecuteResult.Rejected Refusal.CaseErased)
            | _ ->
                let target =
                    match request.Act with
                    | AdmittedAuthorityAct.RegisterActor -> request.OperationId
                    | _ -> view.Target.Actor.Value.ActorId

                RecoveryAdmission.Ready(
                    view.Actor.Actor.Value.ActorId,
                    AuthorityRequests.targetCase request
                    |> Option.map (CaseReferenceId.encode installationId),
                    Some target
                )

    let private propose installationId operationId target action view =
        match targets installationId target action view with
        | Error fault -> AuthorityReviewDecision.Fail fault
        | Ok() ->
            match projection operationId target action view with
            | Error refusal -> AuthorityReviewDecision.Refuse refusal
            | Ok(targetId, projection) -> AuthorityReviewDecision.Proposed(targetId, projection)

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
                view.Read.Actor.Actor
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
                view.Read.ObservedAt
                view.PayloadNonce
                actorId
                operationId
                scope
                bytes
                Array.empty

        LedgerEntries.validate installationId (LedgerEntries.stored entry)
        |> Result.bind (LedgerEntries.receipt principal view.Read.Actor None)
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
        match propose installationId operationId target action view.Read with
        | AuthorityReviewDecision.Fail fault -> LedgerDecision.Fail fault
        | AuthorityReviewDecision.Refuse refusal -> LedgerDecision.Refuse refusal
        | AuthorityReviewDecision.Replay receipt -> LedgerDecision.Replay receipt
        | AuthorityReviewDecision.Proposed(targetId, projection) ->
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
        let identity =
            match action with
            | AuthorityAction.ProvisionOwner ->
                provisionIdentity installationId operationId bytes view
            | AuthorityAction.User _ ->
                admission installationId principal operationId bytes view.Read

        match identity with
        | IdentityDecision.Replay entry ->
            LedgerEntries.receipt principal view.Read.Actor None entry
            |> Result.map LedgerDecision.Replay
            |> Result.defaultWith LedgerDecision.Fail
        | IdentityDecision.Refuse reason -> LedgerDecision.Refuse reason
        | IdentityDecision.Fail fault -> LedgerDecision.Fail fault
        | IdentityDecision.Fresh when view.Head.Sequence = Revision.Maximum ->
            LedgerDecision.Refuse Refusal.LedgerExhausted
        | IdentityDecision.Fresh ->
            fresh installationId principal operationId target action bytes view

    let review
        installationId
        principal
        (request: AdmittedAuthorityRequest)
        (view: AuthorityReadView)
        =
        let bytes = AuthorityRequests.toRecord request |> AuthorityRecord.encode

        match admission installationId principal request.OperationId bytes view with
        | IdentityDecision.Refuse refusal -> AuthorityReviewDecision.Refuse refusal
        | IdentityDecision.Fail fault -> AuthorityReviewDecision.Fail fault
        | IdentityDecision.Replay entry ->
            LedgerEntries.receipt principal view.Actor None entry
            |> Result.map AuthorityReviewDecision.Replay
            |> Result.defaultWith AuthorityReviewDecision.Fail
        | IdentityDecision.Fresh ->
            propose
                installationId
                request.OperationId
                request.Target
                (AuthorityAction.User request.Act)
                view

    let execute
        installationId
        principal
        (request: AdmittedAuthorityRequest)
        (view: AuthorityLockedView)
        =
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
