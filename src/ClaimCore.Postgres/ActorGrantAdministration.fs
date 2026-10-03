namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application

/// Initial ownership is an explicit schema-owner operation against a fresh installation.
/// No login path calls this and the runtime role cannot invoke it through the service API.
module internal ActorGrantAdministration =
    let private observeInitialOwner connection transaction witness principal =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT event_id FROM claimcore.actor_authority_events "
                    + "WHERE revision=1 AND action_name='PROVISION_INITIAL_OWNER'",
                    connection,
                    transaction
                )

            let! stored = command.ExecuteScalarAsync()

            match stored with
            | :? Guid as eventId ->
                let! found =
                    ActorGrantRegistryQueries.existingEvent connection transaction witness eventId

                let! actor = ActorGrantRegistryQueries.targetId connection transaction principal

                return
                    match found, actor with
                    | Some action, Some actorId when
                        action.Principal = Some principal
                        && action.TargetActorId = actorId
                        && action.Grant =
                            Some
                                {
                                    Role = Role.Owner
                                    Scope = GrantScope.Installation
                                }
                        && action.Enabled = Some true
                        && action.ApproverActorId.IsNone
                        ->
                        AuthorityWriteOutcome.Applied(action.EventId, action.Revision)
                    | _ -> AuthorityWriteOutcome.Refused
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let private applyInitialOwner ownerConnection transaction witness principal =
        let actorId = Guid.NewGuid()

        let ownerGrant =
            {
                Role = Role.Owner
                Scope = GrantScope.Installation
            }

        let action: ActorAuthorityAction =
            {
                EventId = Guid.NewGuid()
                Revision = 1L
                ActionName = "PROVISION_INITIAL_OWNER"
                TargetActorId = actorId
                ApproverActorId = None
                Principal = Some principal
                Grant = Some ownerGrant
                Enabled = Some true
            }

        ActorGrantWrite.run ownerConnection transaction witness action (fun () ->
            task {
                do! ActorGrantWrite.insertActor ownerConnection transaction actorId principal 1L

                do! ActorGrantWrite.setGrant ownerConnection transaction actorId ownerGrant true 1L
            })

    let provisionInitialOwner
        (ownerConnection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (principal: PrincipalKey)
        =
        task {
            if not (PrincipalKey.isHuman principal) then
                return AuthorityWriteOutcome.Refused
            else
                OwnerConnection.requireIdentity ownerConnection
                witness.Admit()

                use! _authorityLease =
                    AuthorityOperationFence.acquireShared
                        None
                        ownerConnection
                        CancellationToken.None

                use transaction = ownerConnection.BeginTransaction(IsolationLevel.ReadCommitted)

                let! revision =
                    ActorGrantRead.lockRevision
                        ownerConnection
                        transaction
                        true
                        CancellationToken.None

                if revision <> 0L then
                    return! observeInitialOwner ownerConnection transaction witness principal
                // A restored pre-provisioning primary must not promote another first owner.
                elif witness.Snapshot().TipSequence <> 0L then
                    return AuthorityWriteOutcome.Refused
                else
                    use count =
                        new NpgsqlCommand(
                            "SELECT (SELECT count(*) FROM claimcore.actors) + "
                            + "(SELECT count(*) FROM claimcore.actor_authority_events)",
                            ownerConnection,
                            transaction
                        )

                    let! existing = count.ExecuteScalarAsync()

                    if existing :?> int64 <> 0L then
                        return AuthorityWriteOutcome.Refused
                    else
                        return! applyInitialOwner ownerConnection transaction witness principal
        }
