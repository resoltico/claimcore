namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open DataAuditCommon

module internal DataAuditExportBudgets =
    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*)::bigint,min(key_issuance_ordinal)::bigint,"
                    + "max(key_issuance_ordinal)::bigint,count(DISTINCT key_issuance_ordinal)::bigint,"
                    + "min(key_max_exports)::bigint,max(key_max_exports)::bigint "
                    + "FROM claimcore.recovery_artifact_exports GROUP BY key_id",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    let count = reader.GetInt64(0)

                    if
                        reader.GetInt64(1) <> 1L
                        || reader.GetInt64(2) <> count
                        || reader.GetInt64(3) <> count
                        || reader.GetInt64(4) <> reader.GetInt64(5)
                        || count > reader.GetInt64(5)
                    then
                        corrupt ()
        }
