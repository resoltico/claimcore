namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql

module internal RuntimeSchema =
    let private manifest connection =
        use command =
            new NpgsqlCommand(
                "SELECT version, name, script_sha256 FROM claimcore.schema_migrations ORDER BY version",
                connection
            )

        use reader = command.ExecuteReader()
        let installed = ResizeArray<int * string * string>()

        while reader.Read() do
            installed.Add(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))

        List.ofSeq installed

    let private manifestAsync
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT version, name, script_sha256 FROM claimcore.schema_migrations ORDER BY version",
                    connection
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let installed = ResizeArray<int * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! row = reader.ReadAsync(cancellationToken)
                hasRow <- row

                if row then
                    installed.Add(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))

            return List.ofSeq installed
        }

    let private expected () =
        SchemaDefinition.all ()
        |> List.map (fun migration -> migration.Version, migration.Name, migration.Digest)

    let private preparationSql =
        """
        SELECT
            to_regclass('claimcore.installation_lineage') IS NOT NULL,
            to_regclass('claimcore.request_preparations') IS NOT NULL,
            to_regclass('claimcore.request_preparation_lifecycle') IS NOT NULL,
            to_regclass('claimcore.request_preparation_prunes') IS NOT NULL,
            to_regclass('claimcore.request_preparation_lifecycle_retention') IS NOT NULL,
            to_regclass('claimcore.request_submission_attempts') IS NOT NULL,
            to_regclass('claimcore.request_submission_settlements') IS NOT NULL,
            to_regclass('claimcore.request_submission_legacy_uncertainty') IS NOT NULL,
            to_regclass('claimcore.request_submission_attempts_by_operation') IS NOT NULL,
            (SELECT count(*) = 1 AND bool_and(singleton AND lineage_id <> '00000000-0000-0000-0000-000000000000') FROM claimcore.installation_lineage),
            (SELECT array_agg(a.attname::text ORDER BY a.attnum) = ARRAY[
                'operation_id',
                'canonical_request_format',
                'request_sha256',
                'canonical_request',
                'prepared_at',
                'prepared_application_version',
                'preparing_contract_fingerprint',
                'preparing_contract_kind'
            ] FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'claimcore'
                AND c.relname = 'request_preparations'
                AND a.attnum > 0
                AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparation_lifecycle' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 9 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparation_prunes' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_submission_attempts' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 3 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_submission_settlements' AND a.attnum > 0 AND NOT a.attisdropped),
            (SELECT count(*) = 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_submission_legacy_uncertainty' AND a.attnum > 0 AND NOT a.attisdropped)
        """

    let private requirePreparation (reader: DbDataReader) =
        for index in 0..15 do
            if not (reader.GetBoolean(index)) then
                raise (
                    InvalidDataException($"Required recovery schema component {index} is absent.")
                )

    let requireCompatible (connection: NpgsqlConnection) =
        if manifest connection <> expected () then
            raise RuntimeDatabaseMismatch

        use command = new NpgsqlCommand(preparationSql, connection)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise RuntimeDatabaseMismatch

        requirePreparation reader

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            let! installed = manifestAsync connection cancellationToken

            if installed <> expected () then
                return raise RuntimeDatabaseMismatch

            use command = new NpgsqlCommand(preparationSql, connection)
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! hasRow = reader.ReadAsync(cancellationToken)

            if not hasRow then
                return raise RuntimeDatabaseMismatch

            requirePreparation reader
        }

    let requireCompatibleAsync (connection: NpgsqlConnection) =
        requireCompatibleAsyncWithCancellation connection CancellationToken.None
