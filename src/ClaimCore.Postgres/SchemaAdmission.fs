namespace ClaimCore.Postgres

open System.Data.Common
open System.Threading
open Npgsql

[<RequireQualifiedAccess>]
type internal SchemaAdmissionState =
    | Absent
    | Unsupported
    | IdentityMismatch
    | Current

/// Classify the namespace without executing an old installation's relations or interpreting its rows.
module internal SchemaAdmission =
    let private catalogSql =
        """
        SELECT CASE
            WHEN NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'claimcore') THEN 0
            WHEN EXISTS (
                SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'claimcore' AND c.relname IN ('schema_migrations', 'request_submission_legacy_uncertainty')
            ) THEN 2
            WHEN EXISTS (
                SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'claimcore' AND c.relname = 'schema_baseline'
                    AND c.relkind = 'r' AND c.relowner = n.nspowner
                    AND (SELECT array_agg(a.attname::text ORDER BY a.attnum) = ARRAY[
                        'singleton', 'baseline_id', 'script_sha256', 'installed_at', 'installed_by'
                    ] AND array_agg(a.atttypid::int ORDER BY a.attnum) = ARRAY[16,25,25,1184,25]
                        AND bool_and(a.attnotnull)
                        FROM pg_catalog.pg_attribute a WHERE a.attrelid = c.oid
                            AND a.attnum > 0 AND NOT a.attisdropped)
            ) THEN 1 ELSE 2 END
        """

    let private markerSql =
        "SELECT singleton, baseline_id, script_sha256 FROM claimcore.schema_baseline"

    let private matches (reader: DbDataReader) =
        let expected = SchemaDefinition.current ()

        reader.GetBoolean(0)
        && reader.GetString(1) = expected.Id
        && reader.GetString(2) = expected.Digest

    let private marker (connection: NpgsqlConnection) =
        use command = new NpgsqlCommand(markerSql, connection)
        use reader = command.ExecuteReader()

        if reader.Read() && matches reader && not (reader.Read()) then
            SchemaAdmissionState.Current
        else
            SchemaAdmissionState.IdentityMismatch

    let inspect (connection: NpgsqlConnection) =
        use command = new NpgsqlCommand(catalogSql, connection)

        match command.ExecuteScalar() :?> int with
        | 0 -> SchemaAdmissionState.Absent
        | 1 -> marker connection
        | _ -> SchemaAdmissionState.Unsupported

    let inspectAsync (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(catalogSql, connection)
            let! catalog = command.ExecuteScalarAsync(cancellationToken)

            match catalog :?> int with
            | 0 -> return SchemaAdmissionState.Absent
            | 1 ->
                use query = new NpgsqlCommand(markerSql, connection)
                let! result = query.ExecuteReaderAsync(cancellationToken)
                use reader = result
                let! first = reader.ReadAsync(cancellationToken)

                if not first || not (matches reader) then
                    return SchemaAdmissionState.IdentityMismatch
                else
                    let! extra = reader.ReadAsync(cancellationToken)

                    return
                        if extra then
                            SchemaAdmissionState.IdentityMismatch
                        else
                            SchemaAdmissionState.Current
            | _ -> return SchemaAdmissionState.Unsupported
        }
