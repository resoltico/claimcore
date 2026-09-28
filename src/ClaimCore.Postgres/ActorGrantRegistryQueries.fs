namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

module internal ActorGrantRegistryQueries =
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

    let targetId connection transaction principal =
        task {
            let kind, issuer, stable = PrincipalKey.storageParts principal

            use command =
                new NpgsqlCommand(
                    "SELECT actor_id FROM claimcore.actors WHERE principal_kind=@kind "
                    + "AND issuer=@issuer AND principal_value=@value",
                    connection,
                    transaction
                )

            Sql.text command "kind" kind
            Sql.text command "issuer" issuer
            Sql.text command "value" stable
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? Guid as id -> Some id
                | _ -> None
        }

    let grantState connection transaction targetId (grant: ActorGrant) =
        task {
            let kind, caseId = ActorGrantCandidate.scope grant.Scope

            use command =
                new NpgsqlCommand(
                    "SELECT active FROM claimcore.actor_grants WHERE actor_id=@actor "
                    + "AND scope_kind=@kind AND scope_case_id=@caseId AND role_name=@role",
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
        | GrantScope.Installation -> System.Threading.Tasks.Task.FromResult true
        | GrantScope.Case _ when not active -> System.Threading.Tasks.Task.FromResult true
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

    let remainingOwner connection transaction excludedActor excludedGrant =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.actors a "
                    + "JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
                    + "WHERE a.enabled AND g.active AND g.scope_kind='INSTALLATION' "
                    + "AND g.role_name='OWNER' AND a.actor_id<>@excludedActor "
                    + "AND NOT (g.actor_id=@excludedGrant AND g.role_name='OWNER'))",
                    connection,
                    transaction
                )

            Sql.uuid command "excludedActor" excludedActor
            Sql.uuid command "excludedGrant" excludedGrant
            let! value = command.ExecuteScalarAsync()
            return value :?> bool
        }

    let existingEvent connection transaction (witness: WitnessProtocol) eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,action_name,target_actor_id,approver_actor_id,canonical_action,"
                    + "candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash "
                    + "FROM claimcore.actor_authority_events WHERE event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let revision = reader.GetInt64(0)
                let approver = if reader.IsDBNull(3) then None else Some(reader.GetGuid(3))
                let canonical = reader.GetFieldValue<byte array>(4)

                let action =
                    ActorGrantCandidate.decodeStored
                        canonical
                        revision
                        eventId
                        (reader.GetString(1))
                        (reader.GetGuid(2))
                        approver
                    |> Option.defaultWith (fun () -> raise WitnessPending)

                let digest = reader.GetFieldValue<byte array>(5)
                let sequence = reader.GetInt64(6)
                let epoch = reader.GetInt64(7)
                let hash = reader.GetFieldValue<byte array>(8)

                if reader.Read() || SHA256.HashData(canonical) <> digest then
                    raise WitnessPending

                reader.Close()
                witness.ReconcileAuthority(eventId, sequence, epoch, hash, canonical)
                return Some action
        }
