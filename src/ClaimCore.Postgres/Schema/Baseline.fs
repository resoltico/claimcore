namespace ClaimCore.Postgres

open System
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql

/// The baseline exists, but its immutable installation tuple cannot be admitted.
type internal InstallationConfigurationException() =
    inherit InvalidOperationException("The installation configuration is invalid.")

/// Exact source identity of the only supported installation baseline.
module internal Baseline =
    let identifier = "claimcore-foundation"

    let private bytes =
        let assembly = Assembly.GetExecutingAssembly()

        use stream =
            assembly.GetManifestResourceStream "ClaimCore.Postgres.baseline.sql" |> nonNull

        use buffer = new IO.MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray()

    let digest = SHA256.HashData bytes
    let private sql = UTF8Encoding(false, true).GetString bytes

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

    let private requireIdentity connection token =
        task {
            let! relation =
                DatabaseSession.rows
                    connection
                    "SELECT to_regclass('claimcore.baseline_identity')::text"
                    [||]
                    token

            if DatabaseSession.optional<string> 0 relation.Head |> Option.isNone then
                invalidOp "The schema baseline identity is absent or unsupported."

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT identifier,script_sha256 FROM claimcore.baseline_identity"
                    [||]
                    token

            match rows with
            | [ row ] when
                DatabaseSession.value<string> 0 row = identifier
                && DatabaseSession.value<byte array> 1 row = digest
                ->
                return ()
            | _ -> return invalidOp "The schema baseline differs from this build."
        }

    let private apply connection token =
        task {
            let! _ = DatabaseSession.execute connection sql [||] token

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.baseline_identity(identifier,script_sha256) VALUES ($1,$2)"
                    [| box identifier; box digest |]
                    token

            return ()
        }

    let private installation connection token =
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
            | _ -> return raise (InstallationConfigurationException())
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

    let require connection token =
        task {
            do! requireIdentity connection token
            let! id, timeZone = installation connection token

            try
                zone timeZone |> ignore
            with :? ArgumentException ->
                raise (InstallationConfigurationException())

            if id = Guid.Empty then
                raise (InstallationConfigurationException())

            return id, timeZone
        }

    let private install connection timeZone token =
        task {
            do! apply connection token
            let id = Guid.NewGuid()

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.installation(installation_id,business_time_zone) VALUES ($1,$2)"
                    [| box id; box timeZone |]
                    token

            return id
        }

    let initialize (source: NpgsqlDataSource) timeZone token =
        task {
            let timeZone = zone timeZone

            return!
                DatabaseSession.transaction
                    source
                    Data.IsolationLevel.ReadCommitted
                    token
                    (fun connection transaction ->
                        task {
                            ServerSupport.require connection.PostgreSqlVersion
                            // CLAIMCOR: serialize namespace discovery and installation, even before tables exist.
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    "SELECT pg_advisory_xact_lock(4849322681855856466)"
                                    [||]
                                    token

                            let! present = exists connection token

                            if present then
                                let! id, storedZone = require connection token

                                if storedZone <> timeZone then
                                    invalidOp "The installation configuration differs."

                                return id
                            else
                                let! id = install connection timeZone token
                                do! DatabaseSession.commit transaction token
                                return id
                        })
        }

/// Owner-only schema initialization; no upgrade, adoption or conversion is provided.
module internal SchemaAdministration =
    let initialize source timeZone cancellationToken =
        Baseline.initialize source timeZone cancellationToken
