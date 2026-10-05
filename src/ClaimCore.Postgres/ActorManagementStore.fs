namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open ActorGrantRegistryQueries

/// Owner actions use the OIDC principal bound at Runtime.ForActor. The event ID is caller-owned
/// and exact retries never mint a replacement after an uncertain witness/primary outcome.
type internal PostgresActorManagement
    (dataSource: NpgsqlDataSource, witness: WitnessProtocol, ownerPrincipal: PrincipalKey) =
    let sameIssuer target =
        let _, issuer, _ = PrincipalKey.storageParts ownerPrincipal
        let _, targetIssuer, _ = PrincipalKey.storageParts target
        String.Equals(issuer, targetIssuer, StringComparison.Ordinal)

    let result eventId targetId =
        function
        | AuthorityWriteOutcome.Applied(_, revision) ->
            ActorManagementOutcome.Applied(eventId, revision, targetId)
        | AuthorityWriteOutcome.Refused -> ActorManagementOutcome.ResourceUnavailable
        | AuthorityWriteOutcome.Unconfirmed _ -> ActorManagementOutcome.Unconfirmed eventId

    let run eventId ct action =
        task {
            if eventId = Guid.Empty then
                return ActorManagementOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                    use! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                    let! revision = ActorGrantRead.lockRevision connection transaction true ct

                    let! owner = loadApprover connection transaction revision ownerPrincipal

                    match owner with
                    | None -> return ActorManagementOutcome.ResourceUnavailable
                    | Some authorized ->
                        return! action connection transaction revision authorized.ActorId
                with
                | WitnessPending -> return ActorManagementOutcome.Unconfirmed eventId
                | :? OperationCanceledException -> return ActorManagementOutcome.Unconfirmed eventId
                | _ -> return ActorManagementOutcome.Unconfirmed eventId
        }

    let matching eventId expected (action: ActorAuthorityAction) approverId targetId =
        action.EventId = eventId
        && action.ActionName = expected
        && action.ApproverActorId = Some approverId
        && (targetId |> Option.forall ((=) action.TargetActorId))

    let registered eventId target ct connection transaction revision approverId =
        task {
            let! seen = existingEvent connection transaction witness eventId ct

            match seen with
            | Some action when
                matching eventId "REGISTER_ACTOR" action approverId None
                && action.Principal = Some target
                ->
                return
                    ActorManagementOutcome.Applied(eventId, action.Revision, action.TargetActorId)
            | Some _ -> return ActorManagementOutcome.ResourceUnavailable
            | None ->
                let! known = targetId connection transaction target

                if known.IsSome then
                    return ActorManagementOutcome.ResourceUnavailable
                else
                    let actorId = Guid.NewGuid()

                    let action: ActorAuthorityAction =
                        {
                            EventId = eventId
                            Revision = revision + 1L
                            ActionName = "REGISTER_ACTOR"
                            TargetActorId = actorId
                            ApproverActorId = Some approverId
                            Principal = Some target
                            Grant = None
                            Enabled = Some true
                        }

                    let! applied =
                        ActorGrantWrite.run
                            connection
                            transaction
                            witness
                            action
                            (fun () ->
                                ActorGrantWrite.insertActor
                                    connection
                                    transaction
                                    actorId
                                    target
                                    action.Revision)
                            ct

                    return result eventId actorId applied
        }

    let setEnabled eventId target enabled ct connection transaction revision approverId =
        task {
            let! target = targetId connection transaction target

            match target with
            | None -> return ActorManagementOutcome.ResourceUnavailable
            | Some actorId ->
                let! seen = existingEvent connection transaction witness eventId ct
                let actionName = if enabled then "ENABLE_ACTOR" else "DISABLE_ACTOR"

                match seen with
                | Some action when
                    matching eventId actionName action approverId (Some actorId)
                    && action.Enabled = Some enabled
                    ->
                    return ActorManagementOutcome.Applied(eventId, action.Revision, actorId)
                | Some _ -> return ActorManagementOutcome.ResourceUnavailable
                | None ->
                    let! current = targetState connection transaction actorId

                    let! ownerSafe =
                        if enabled then
                            Task.FromResult true
                        else
                            remainingOwner connection transaction actorId Guid.Empty

                    match current with
                    | Some(state, _) when state <> enabled && ownerSafe ->
                        let action =
                            ActorGrantCandidate.enabledAction
                                eventId
                                (revision + 1L)
                                actorId
                                approverId
                                enabled

                        let! applied =
                            ActorGrantWrite.run
                                connection
                                transaction
                                witness
                                action
                                (fun () ->
                                    ActorGrantWrite.setEnabled
                                        connection
                                        transaction
                                        actorId
                                        enabled
                                        action.Revision)
                                ct

                        return result eventId actorId applied
                    | _ -> return ActorManagementOutcome.ResourceUnavailable
        }

    interface IActorManagement with
        member _.RegisterActor(eventId, target, ct) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run eventId ct (registered eventId target ct)

        member _.SetGrant(eventId, target, role, scope, active, ct) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run
                    eventId
                    ct
                    (ActorManagementGrant.setGrant witness eventId target role scope active ct)

        member _.SetEnabled(eventId, target, enabled, ct) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run eventId ct (setEnabled eventId target enabled ct)

        member _.Observe(eventId, ct) =
            run eventId ct (fun connection transaction _ _ ->
                task {
                    let! seen = existingEvent connection transaction witness eventId ct

                    match seen with
                    | Some action ->
                        return
                            ActorManagementOutcome.Applied(
                                eventId,
                                action.Revision,
                                action.TargetActorId
                            )
                    | None ->
                        let! evidence = witness.EvidenceStore.TryReadEvidence(eventId, Intent, ct)
                        let pending = evidence.IsSome

                        return
                            if pending then
                                ActorManagementOutcome.Unconfirmed eventId
                            else
                                ActorManagementOutcome.ResourceUnavailable
                })
