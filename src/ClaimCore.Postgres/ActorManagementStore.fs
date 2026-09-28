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

    let run eventId action =
        task {
            if eventId = Guid.Empty then
                return ActorManagementOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    let! revision =
                        ActorGrantRead.lockRevision
                            connection
                            transaction
                            true
                            CancellationToken.None

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

    let registered eventId target connection transaction revision approverId =
        task {
            let! seen = existingEvent connection transaction witness eventId

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
                        ActorGrantWrite.run connection transaction witness action (fun () ->
                            ActorGrantWrite.insertActor
                                connection
                                transaction
                                actorId
                                target
                                action.Revision)

                    return result eventId actorId applied
        }

    let setEnabled eventId target enabled connection transaction revision approverId =
        task {
            let! target = targetId connection transaction target

            match target with
            | None -> return ActorManagementOutcome.ResourceUnavailable
            | Some actorId ->
                let! seen = existingEvent connection transaction witness eventId
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
                        let action: ActorAuthorityAction =
                            {
                                EventId = eventId
                                Revision = revision + 1L
                                ActionName = actionName
                                TargetActorId = actorId
                                ApproverActorId = Some approverId
                                Principal = None
                                Grant = None
                                Enabled = Some enabled
                            }

                        let! applied =
                            ActorGrantWrite.run connection transaction witness action (fun () ->
                                ActorGrantWrite.setEnabled
                                    connection
                                    transaction
                                    actorId
                                    enabled
                                    action.Revision)

                        return result eventId actorId applied
                    | _ -> return ActorManagementOutcome.ResourceUnavailable
        }

    interface IActorManagement with
        member _.RegisterActor(eventId, target, _) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run eventId (registered eventId target)

        member _.SetGrant(eventId, target, role, scope, active, _) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run eventId (ActorManagementGrant.setGrant witness eventId target role scope active)

        member _.SetEnabled(eventId, target, enabled, _) =
            if not (sameIssuer target) then
                Task.FromResult ActorManagementOutcome.ResourceUnavailable
            else
                run eventId (setEnabled eventId target enabled)

        member _.Observe(eventId, _) =
            run eventId (fun connection transaction _ _ ->
                task {
                    let! seen = existingEvent connection transaction witness eventId

                    match seen with
                    | Some action ->
                        return
                            ActorManagementOutcome.Applied(
                                eventId,
                                action.Revision,
                                action.TargetActorId
                            )
                    | None ->
                        let pending = witness.EvidenceStore.TryReadEvidence(eventId, Intent).IsSome

                        return
                            if pending then
                                ActorManagementOutcome.Unconfirmed eventId
                            else
                                ActorManagementOutcome.ResourceUnavailable
                })
