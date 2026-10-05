namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ActorGrantOwnerSafety

/// All changes hold authority_tip FOR UPDATE across witness intent and primary COMMIT.
/// Case mutations acquire the same lock before operation/case locks; their caller must recheck
/// the exact actor and revision under that lock before accepting a business transition.
type internal ActorGrantRegistry(dataSource: NpgsqlDataSource, witness: WitnessProtocol) =
    let loadApprover connection transaction revision principal =
        task {
            let! actor =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    principal
                    ResourceScope.Installation
                    revision
                    CancellationToken.None

            return
                actor
                |> Option.filter (fun authority ->
                    ActorAuthorization.can
                        principal
                        authority
                        EndpointAction.ManageGrants
                        ResourceScope.Installation)
        }

    let targetState connection transaction targetId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT enabled,principal_kind FROM claimcore.actors WHERE actor_id=@target",
                    connection,
                    transaction
                )

            Sql.uuid command "target" targetId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            return
                if found then
                    Some(reader.GetBoolean(0), reader.GetString(1))
                else
                    None
        }

    let grantState connection transaction targetId (grant: ActorGrant) =
        task {
            let kind, caseId = ActorGrantCandidate.scope grant.Scope

            use command =
                new NpgsqlCommand(
                    "SELECT active FROM claimcore.actor_grants "
                    + "WHERE actor_id=@actor AND scope_kind=@kind AND scope_case_id=@caseId AND role_name=@role",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" targetId
            Sql.text command "kind" kind
            Sql.uuid command "caseId" caseId
            Sql.text command "role" (ActorGrantCandidate.roleName grant.Role)
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? bool as active -> Some active
                | _ -> None
        }

    let caseExists connection transaction active =
        function
        | GrantScope.Installation -> Task.FromResult true
        | GrantScope.Case _ when not active -> Task.FromResult true
        | GrantScope.Case caseId ->
            task {
                use command =
                    new NpgsqlCommand(
                        "SELECT EXISTS(SELECT 1 FROM claimcore.cases c WHERE c.case_id=@caseId "
                        + "AND c.privacy_phase='ACTIVE' AND NOT EXISTS "
                        + "(SELECT 1 FROM claimcore.case_erasure_tombstones t WHERE t.case_id=c.case_id))",
                        connection,
                        transaction
                    )

                Sql.uuid command "caseId" caseId
                let! value = command.ExecuteScalarAsync()
                return value :?> bool
            }

    let writeGrant connection transaction revision approverId targetId grant active =
        task {
            let! safe =
                if active || grant.Role <> Role.Owner || grant.Scope <> GrantScope.Installation then
                    Task.FromResult true
                else
                    remainingOwner connection transaction Guid.Empty targetId

            if not safe then
                return AuthorityWriteOutcome.Refused
            else
                let action: ActorAuthorityAction =
                    {
                        EventId = Guid.NewGuid()
                        Revision = revision + 1L
                        ActionName = if active then "GRANT_ROLE" else "REVOKE_ROLE"
                        TargetActorId = targetId
                        ApproverActorId = Some approverId
                        Principal = None
                        Grant = Some grant
                        Enabled = Some active
                    }

                return!
                    ActorGrantWrite.run
                        connection
                        transaction
                        witness
                        action
                        (fun () ->
                            ActorGrantWrite.setGrant
                                connection
                                transaction
                                targetId
                                grant
                                active
                                action.Revision)
                        CancellationToken.None
        }

    member _.RegisterActor(approverPrincipal: PrincipalKey, targetPrincipal: PrincipalKey) =
        task {
            do! witness.Admit(CancellationToken.None)
            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            use! _authorityLease =
                AuthorityOperationFence.acquireShared
                    (Some dataSource)
                    connection
                    CancellationToken.None

            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! approver = loadApprover connection transaction revision approverPrincipal

            match approver with
            | None -> return AuthorityWriteOutcome.Refused
            | Some authority ->
                return!
                    ActorRegistrationWrite.register
                        connection
                        transaction
                        witness
                        authority.ActorId
                        revision
                        targetPrincipal
        }

    member _.SetEnabled(approverPrincipal: PrincipalKey, targetId: Guid, enabled: bool) =
        task {
            if targetId = Guid.Empty then
                return AuthorityWriteOutcome.Refused
            else
                do! witness.Admit(CancellationToken.None)
                use! connection = RuntimeDatabase.openConnectionAsync dataSource

                use! _authorityLease =
                    AuthorityOperationFence.acquireShared
                        (Some dataSource)
                        connection
                        CancellationToken.None

                use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                let! revision =
                    ActorGrantRead.lockRevision connection transaction true CancellationToken.None

                let! approver = loadApprover connection transaction revision approverPrincipal
                let! current = targetState connection transaction targetId

                match approver, current with
                | Some authority, Some(currentEnabled, _) when currentEnabled <> enabled ->
                    let! safe =
                        if enabled then
                            Task.FromResult true
                        else
                            remainingOwner connection transaction targetId Guid.Empty

                    if not safe then
                        return AuthorityWriteOutcome.Refused
                    else
                        let action =
                            ActorGrantCandidate.enabledAction
                                (Guid.NewGuid())
                                (revision + 1L)
                                targetId
                                authority.ActorId
                                enabled

                        return!
                            ActorGrantWrite.run
                                connection
                                transaction
                                witness
                                action
                                (fun () ->
                                    ActorGrantWrite.setEnabled
                                        connection
                                        transaction
                                        targetId
                                        enabled
                                        action.Revision)
                                CancellationToken.None
                | _ -> return AuthorityWriteOutcome.Refused
        }

    member _.SetGrant
        (approverPrincipal: PrincipalKey, targetId: Guid, grant: ActorGrant, active: bool)
        =
        task {
            let _, caseId = ActorGrantCandidate.scope grant.Scope

            if
                targetId = Guid.Empty
                || (grant.Scope <> GrantScope.Installation && caseId = Guid.Empty)
            then
                return AuthorityWriteOutcome.Refused
            else
                do! witness.Admit(CancellationToken.None)
                use! connection = RuntimeDatabase.openConnectionAsync dataSource

                use! _authorityLease =
                    AuthorityOperationFence.acquireShared
                        (Some dataSource)
                        connection
                        CancellationToken.None

                use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                let! revision =
                    ActorGrantRead.lockRevision connection transaction true CancellationToken.None

                let! approver = loadApprover connection transaction revision approverPrincipal
                let! target = targetState connection transaction targetId
                let! validCase = caseExists connection transaction active grant.Scope
                let! current = grantState connection transaction targetId grant

                match approver, target with
                | Some authority, Some(_, kind) when
                    validCase
                    && current <> Some active
                    && (active || current.IsSome)
                    && (kind = "HUMAN"
                        || (grant.Role <> Role.Owner && grant.Role <> Role.DataSteward))
                    ->
                    return!
                        writeGrant
                            connection
                            transaction
                            revision
                            authority.ActorId
                            targetId
                            grant
                            active
                | _ -> return AuthorityWriteOutcome.Refused
        }
