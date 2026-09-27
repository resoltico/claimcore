namespace ClaimCore.Postgres

open System
open Npgsql

/// Refuses actor/grant changes that would remove the installation's last enabled owner.
module internal ActorGrantOwnerSafety =
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
