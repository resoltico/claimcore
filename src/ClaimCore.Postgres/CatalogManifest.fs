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

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            let dateStyle, searchPath = currentSettings connection

            try
                setSettings connection "ISO, YMD" "pg_catalog"

                use versionCommand =
                    new NpgsqlCommand(
                        "SELECT current_setting('server_version_num')::integer",
                        connection
                    )

                let! version = versionCommand.ExecuteScalarAsync(cancellationToken)
                use command = new NpgsqlCommand(sql.Value, connection)
                let! result = command.ExecuteScalarAsync(cancellationToken)

                let catalog =
                    match result with
                    | :? string as value -> value
                    | _ -> raise (InvalidDataException("The live catalog projection is missing."))

                require (version :?> int) catalog
            finally
                setSettings connection dateStyle searchPath
        }
