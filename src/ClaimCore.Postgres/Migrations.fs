namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open Npgsql
open ClaimCore.Application

/// Ordered migrations are checksum-bound and recorded atomically with their schema changes.
module Migrations =
    let currentVersion () = SchemaDefinition.currentVersion ()

    let private journalExists (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command =
            new NpgsqlCommand(
                "SELECT to_regnamespace('claimcore') IS NOT NULL, to_regclass('claimcore.schema_migrations') IS NOT NULL",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise (InvalidDataException("Database catalog state could not be read."))

        reader.GetBoolean(0), reader.GetBoolean(1)

    let private createJournal (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command =
            new NpgsqlCommand(
                """
                CREATE SCHEMA claimcore;
                REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;
                CREATE TABLE claimcore.schema_migrations (
                    version integer PRIMARY KEY CHECK (version > 0),
                    name text NOT NULL UNIQUE CHECK (name ~ '^[0-9]{3}_[a-z0-9_]+$'),
                    script_sha256 text NOT NULL CHECK (script_sha256 ~ '^[0-9a-f]{64}$'),
                    applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    applied_by text NOT NULL DEFAULT session_user
                );
                REVOKE ALL ON TABLE claimcore.schema_migrations FROM PUBLIC;
                """,
                connection,
                transaction
            )

        command.ExecuteNonQuery() |> ignore

    let private installedMigrations
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        =
        use command =
            new NpgsqlCommand(
                "SELECT version, name, script_sha256 FROM claimcore.schema_migrations ORDER BY version",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()
        let installed = ResizeArray<int * string * string>()

        while reader.Read() do
            installed.Add(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))

        List.ofSeq installed

    let private validateInstalled
        (expected: MigrationDefinition list)
        (installed: (int * string * string) list)
        =
        if installed.Length > expected.Length then
            raise (
                InvalidDataException(
                    "The database contains migrations newer than this application."
                )
            )

        List.zip installed (expected |> List.take installed.Length)
        |> List.iter (fun ((version, name, digest), migration) ->
            if
                version <> migration.Version
                || name <> migration.Name
                || digest <> migration.Digest
            then
                raise (
                    InvalidDataException(
                        "An installed migration name or checksum does not match this application."
                    )
                ))

    let private applyMigration
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (migration: MigrationDefinition)
        =
        use command = new NpgsqlCommand(migration.Script, connection, transaction)
        command.ExecuteNonQuery() |> ignore

        use record =
            new NpgsqlCommand(
                "INSERT INTO claimcore.schema_migrations (version, name, script_sha256) VALUES (@version, @name, @digest)",
                connection,
                transaction
            )

        record.Parameters.AddWithValue("version", migration.Version) |> ignore
        Sql.text record "name" migration.Name
        Sql.text record "digest" migration.Digest

        if record.ExecuteNonQuery() <> 1 then
            raise (InvalidDataException("A migration journal row was not inserted."))

    let internal ownerBuilder (connectionString: string) =
        let builder = NpgsqlConnectionStringBuilder(connectionString)

        if
            String.IsNullOrWhiteSpace(builder.Username)
            || builder.Username = "claimcore_app"
            || not (String.IsNullOrWhiteSpace(builder.Options))
            || builder.NoResetOnClose
            || builder.LogParameters
            || builder.PersistSecurityInfo
        then
            invalidArg
                (nameof connectionString)
                "Use an explicit owner login without startup options, parameter logging, retained secrets, or pool reset bypasses."

        builder.Enlist <- false
        builder.Timeout <- 5
        builder.CommandTimeout <- 30
        builder.IncludeErrorDetail <- false
        builder.LogParameters <- false
        builder.PersistSecurityInfo <- false
        builder.NoResetOnClose <- false

        builder.ApplicationName <-
            BuildIdentity.current.Product
            + "/"
            + BuildIdentity.current.Version
            + " migrator"

        builder

    let internal requireOwnerIdentity (connection: NpgsqlConnection) =
        use identity =
            new NpgsqlCommand(
                "SELECT session_user = current_user AND current_user <> 'claimcore_app'",
                connection
            )

        if not (identity.ExecuteScalar() :?> bool) then
            raise RuntimeDatabaseMismatch

    let private ensureJournal connection transaction =
        let schemaExists, hasJournal = journalExists connection transaction

        match schemaExists, hasJournal with
        | false, false -> createJournal connection transaction
        | true, true -> ()
        | false, true ->
            raise (InvalidDataException("The migration journal exists without its owning schema."))
        | true, false ->
            raise (
                InvalidDataException(
                    "The claimcore schema predates the ordered migration journal; no implicit adoption is allowed."
                )
            )

    let private migrate (connection: NpgsqlConnection) =
        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
        Sql.lockKey connection transaction "claimcore:schema"
        ensureJournal connection transaction
        let expected = SchemaDefinition.all ()
        let installed = installedMigrations connection transaction
        validateInstalled expected installed

        expected
        |> List.skip installed.Length
        |> List.iter (applyMigration connection transaction)

        transaction.Commit()

    let internal requireCurrent (connection: NpgsqlConnection) =
        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
        Sql.lockKey connection transaction "claimcore:schema"
        ensureJournal connection transaction
        let expected = SchemaDefinition.all ()
        let installed = installedMigrations connection transaction
        validateInstalled expected installed

        if installed.Length <> expected.Length then
            raise (InvalidDataException("The database has pending ordered migrations."))

        transaction.Commit()

    let apply (connectionString: string) =
        let builder = ownerBuilder connectionString
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        DatabaseEnvironment.requireCompatible connection
        requireOwnerIdentity connection
        migrate connection
