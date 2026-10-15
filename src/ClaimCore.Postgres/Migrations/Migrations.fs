namespace ClaimCore.Postgres

open System
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql

/// An ordered, checksum-bound schema script. Applied history is never rewritten.
type internal Migration =
    {
        Version: int
        Name: string
        Sql: string
        Digest: byte array
    }

module internal Migrations =
    let scripts =
        let assembly = Assembly.GetExecutingAssembly()

        assembly.GetManifestResourceNames()
        |> Array.filter _.EndsWith(".sql", StringComparison.Ordinal)
        |> Array.sort
        |> Array.map (fun name ->
            use stream = assembly.GetManifestResourceStream name |> nonNull
            use buffer = new IO.MemoryStream()
            stream.CopyTo buffer
            let bytes = buffer.ToArray()
            let sql = UTF8Encoding(false, true).GetString bytes

            let file = name.Split('.') |> Array.rev |> Array.item 1
            let parts = file.Split('_', 2)

            {
                Version = Int32.Parse parts[0]
                Name = parts[1]
                Sql = sql
                Digest = SHA256.HashData bytes
            })
        |> Array.toList

    let exists connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM pg_namespace WHERE nspname='claimcore')"
                    [||]
                    token

            return DatabaseSession.value<bool> 0 rows.Head
        }

    let history connection (scripts: Migration list) token =
        task {
            let! relation =
                DatabaseSession.rows
                    connection
                    "SELECT to_regclass('claimcore.schema_migrations')::text"
                    [||]
                    token

            if DatabaseSession.optional<string> 0 relation.Head |> Option.isNone then
                invalidOp "Schema migration history is absent or unsupported."

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT version,name,script_sha256 FROM claimcore.schema_migrations ORDER BY version"
                    [||]
                    token

            if rows.IsEmpty || rows.Length > scripts.Length then
                invalidOp "Schema migration history is absent or unsupported."

            for row, script in List.zip rows (scripts |> List.take rows.Length) do
                if
                    DatabaseSession.value<int> 0 row <> script.Version
                    || DatabaseSession.value<string> 1 row <> script.Name
                    || DatabaseSession.value<byte array> 2 row <> script.Digest
                then
                    invalidOp "Schema migration history differs from the compiled scripts."

            return rows.Length
        }

    let apply connection script token =
        task {
            let! _ = DatabaseSession.execute connection script.Sql [||] token

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.schema_migrations(version,name,script_sha256) VALUES ($1,$2,$3)"
                    [| box script.Version; box script.Name; box script.Digest |]
                    token

            return ()
        }

    let installation connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT installation_id,business_time_zone FROM claimcore.installation"
                    [||]
                    token

            match rows with
            | [ row ] ->
                return DatabaseSession.value<Guid> 0 row, DatabaseSession.value<string> 1 row
            | _ -> return invalidOp "The installation row is missing."
        }

    let zone (value: string) =
        let eligible =
            value = "Etc/UTC"
            || TimeZoneInfo.GetSystemTimeZones() |> Seq.exists (fun zone -> zone.Id = value)

        if not eligible || value = "UTC" then
            invalidArg (nameof value) "Use a canonical IANA business time zone."

        let resolved = TimeZoneInfo.FindSystemTimeZoneById value

        if not resolved.HasIanaId || resolved.Id <> value then
            invalidArg (nameof value) "Use a resolvable IANA business time zone."

        value

    let initialize (source: NpgsqlDataSource) timeZone token =
        task {
            let timeZone = zone timeZone

            return!
                DatabaseSession.transaction
                    source
                    Data.IsolationLevel.Serializable
                    token
                    (fun connection transaction ->
                        task {
                            ServerSupport.require connection.PostgreSqlVersion
                            let! exists = exists connection token

                            if exists then
                                let! count = history connection scripts token
                                let! id, storedZone = installation connection token

                                if count <> scripts.Length || storedZone <> timeZone then
                                    invalidOp "The installation configuration differs."

                                return id
                            else
                                for script in scripts do
                                    do! apply connection script token

                                let id = Guid.NewGuid()

                                let! _ =
                                    DatabaseSession.execute
                                        connection
                                        "INSERT INTO claimcore.installation(installation_id,business_time_zone) VALUES ($1,$2)"
                                        [| box id; box timeZone |]
                                        token

                                do! DatabaseSession.commit transaction token
                                return id
                        })
        }

    let migrateWith (source: NpgsqlDataSource) scripts token =
        task {
            use! connection = source.OpenConnectionAsync(token)
            ServerSupport.require connection.PostgreSqlVersion
            let! applied = history connection scripts token

            for script in scripts |> List.skip applied do
                use! transaction = connection.BeginTransactionAsync(token)
                let! current = history connection scripts token

                if current <> script.Version - 1 then
                    invalidOp "Migration history moved."

                do! apply connection script token
                do! DatabaseSession.commit transaction token
        }

/// Owner-only schema administration; a runtime host has no reason to call these operations.
module internal SchemaAdministration =
    let initialize source timeZone cancellationToken =
        Migrations.initialize source timeZone cancellationToken

    let migrate source cancellationToken =
        Migrations.migrateWith source Migrations.scripts cancellationToken
