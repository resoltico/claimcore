namespace ClaimCore.Postgres

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open Npgsql

/// A source-pinned projection of a pristine installation's live PostgreSQL catalog.
module internal CatalogManifest =
    let private resource name =
        let assembly = Assembly.GetExecutingAssembly()

        match assembly.GetManifestResourceStream(name) |> Option.ofObj with
        | None -> raise (InvalidDataException("A catalog manifest resource is missing."))
        | Some stream ->
            use source = stream
            use reader = new StreamReader(source)
            reader.ReadToEnd()

    let private expected =
        lazy
            (use document = JsonDocument.Parse(resource "ClaimCore.CatalogManifest.json")
             let root = document.RootElement

             if
                 root.ValueKind <> JsonValueKind.Object
                 || (root.EnumerateObject() |> Seq.length) <> 3
                 || root.GetProperty("schemaVersion").GetInt32() <> 1
                 || root.GetProperty("catalog").ValueKind <> JsonValueKind.Array
             then
                 raise (InvalidDataException("The pinned catalog manifest is malformed."))

             root.GetProperty("serverVersionNum").GetInt32(),
             JsonSerializer.Serialize(root.GetProperty("catalog")))

    let private sql = lazy (resource "ClaimCore.CatalogManifest.sql")

    let private require version (catalog: string) =
        let expectedVersion, expectedCatalog = expected.Value

        if version <> expectedVersion then
            raise (InvalidDataException("This PostgreSQL build has no reviewed catalog manifest."))

        use observed = JsonDocument.Parse(catalog)
        use pinned = JsonDocument.Parse(expectedCatalog)

        if not (JsonElement.DeepEquals(observed.RootElement, pinned.RootElement)) then
            raise (
                InvalidDataException(
                    "The live PostgreSQL catalog differs from the pinned baseline."
                )
            )

    let private currentSettings (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT current_setting('DateStyle'), current_setting('search_path')",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise (InvalidDataException("The catalog session settings are missing."))

        reader.GetString(0), reader.GetString(1)

    let private setSettings (connection: NpgsqlConnection) dateStyle searchPath =
        use command =
            new NpgsqlCommand(
                "SELECT set_config('DateStyle', @dateStyle, false), "
                + "set_config('search_path', @searchPath, false)",
                connection
            )

        command.Parameters.AddWithValue("dateStyle", dateStyle) |> ignore
        command.Parameters.AddWithValue("searchPath", searchPath) |> ignore
        command.ExecuteNonQuery() |> ignore

    let requireCompatible (connection: NpgsqlConnection) =
        let dateStyle, searchPath = currentSettings connection

        try
            setSettings connection "ISO, YMD" "pg_catalog"

            use versionCommand =
                new NpgsqlCommand(
                    "SELECT current_setting('server_version_num')::integer",
                    connection
                )

            let version = versionCommand.ExecuteScalar() :?> int
            use command = new NpgsqlCommand(sql.Value, connection)

            let catalog =
                match command.ExecuteScalar() with
                | :? string as value -> value
                | _ -> raise (InvalidDataException("The live catalog projection is missing."))

            require version catalog
        finally
            setSettings connection dateStyle searchPath

    let private rollbackAdmission (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        task {
            use cleanup = new CancellationTokenSource(TimeSpan.FromSeconds 5.)

            try
                do! transaction.RollbackAsync(cleanup.Token)
                do! transaction.DisposeAsync().AsTask().WaitAsync(cleanup.Token)
                return None
            with error ->
                // Never put a session whose transaction cleanup is unknown back into its pool.
                try
                    NpgsqlConnection.ClearPool(connection)
                    do! connection.CloseAsync().WaitAsync(cleanup.Token)
                    do! transaction.DisposeAsync().AsTask().WaitAsync(cleanup.Token)
                with invalidation ->
                    error.Data["CatalogInvalidationFailure"] <- invalidation.GetType().Name

                return Some error
        }

    let private verifyCatalogAsync
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        =
        task {
            use settings =
                new NpgsqlCommand(
                    "SET LOCAL DateStyle = 'ISO, YMD'; SET LOCAL search_path = pg_catalog",
                    connection,
                    transaction
                )

            let! _ = settings.ExecuteNonQueryAsync(cancellationToken)

            use versionCommand =
                new NpgsqlCommand(
                    "SELECT current_setting('server_version_num')::integer",
                    connection,
                    transaction
                )

            let! version = versionCommand.ExecuteScalarAsync(cancellationToken)
            use command = new NpgsqlCommand(sql.Value, connection, transaction)
            let! result = command.ExecuteScalarAsync(cancellationToken)

            let catalog =
                match result with
                | :? string as value -> value
                | _ -> raise (InvalidDataException("The live catalog projection is missing."))

            require (version :?> int) catalog
        }

    /// Pre-work admission owns a transaction; local settings disappear on rollback.
    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            let! transaction = connection.BeginTransactionAsync(cancellationToken)
            let mutable failure: exn option = None

            try
                do! verifyCatalogAsync connection transaction cancellationToken
            with error ->
                failure <- Some error

            let! cleanupFailure = rollbackAdmission connection transaction

            match failure, cleanupFailure with
            | Some original, cleanup ->
                if cleanup.IsSome then
                    original.Data["CatalogAdmissionCleanup"] <- "Connection invalidated."

                return raise original
            | None, Some cleanup -> return raise cleanup
            | None, None -> return ()
        }
