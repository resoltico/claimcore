namespace ClaimCore.Postgres

open System.Data
open System.Threading
open Npgsql

[<RequireQualifiedAccess>]
type internal DualControlRoster =
    | Ready of grantRevision: int64
    | Missing

/// A roster prerequisite only; live issuer, full authority audit, separate custodians and
/// off-host witness/backup qualification are independent real-data admission requirements.
module internal ActorGrantDeployment =
    let verifyRoster (dataSource: NpgsqlDataSource) =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision connection transaction false CancellationToken.None

            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.actors owner_actor "
                    + "JOIN claimcore.actor_grants owner_grant ON owner_grant.actor_id=owner_actor.actor_id "
                    + "WHERE owner_actor.enabled AND owner_actor.principal_kind='HUMAN' "
                    + "AND owner_grant.active AND owner_grant.scope_kind='INSTALLATION' "
                    + "AND owner_grant.role_name='OWNER' AND EXISTS ("
                    + "SELECT 1 FROM claimcore.actors second_actor "
                    + "JOIN claimcore.actor_grants second_grant ON second_grant.actor_id=second_actor.actor_id "
                    + "WHERE second_actor.actor_id<>owner_actor.actor_id "
                    + "AND second_actor.enabled AND second_actor.principal_kind='HUMAN' "
                    + "AND second_grant.active AND second_grant.scope_kind='INSTALLATION' "
                    + "AND second_grant.role_name IN ('OWNER','DATA_STEWARD')))",
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync()

            return
                if value :?> bool then
                    DualControlRoster.Ready revision
                else
                    DualControlRoster.Missing
        }
