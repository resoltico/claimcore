namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open DataAuditCommon

/// Every generated managed-copy event must have its paired immutable export record.
module internal DataAuditExportIntegrity =
    let rejectOrphanProductEvents
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copy_events m LEFT JOIN "
                    + "claimcore.recovery_artifact_exports e ON e.export_id=m.copy_id "
                    + "WHERE m.producer_kind='PRODUCT_EXPORT' AND e.export_id IS NULL)",
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync(cancellationToken)

            if unbox<bool> value then
                corrupt ()
        }
