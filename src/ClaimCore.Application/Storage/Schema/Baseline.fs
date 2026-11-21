namespace ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts

/// Exact source identity of the only supported installation baseline.
module internal Baseline =
    let identifier = "claimcore-format-7"

    let private bytes =
        let assembly = Assembly.GetExecutingAssembly()

        use stream =
            assembly.GetManifestResourceStream "ClaimCore.Application.baseline.sql"
            |> nonNull

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
                raise (OperatorFaultException OperatorFault.SchemaMismatch)

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
            | _ -> return raise (OperatorFaultException OperatorFault.SchemaMismatch)
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
                    ("SELECT installation_id,business_time_zone,installation_format, "
                     + "(SELECT months FROM claimcore.disclosure_retention WHERE singleton) "
                     + "FROM claimcore.installation")
                    [||]
                    token

            match rows with
            | [ row ] when DatabaseSession.value<int16> 2 row = int16 InstallationFormat.Current ->
                if DatabaseSession.optional<int> 3 row <> Some DisclosureTrail.retentionMonths then
                    raise (InstallationConfigurationException())

                return DatabaseSession.value<Guid> 0 row, DatabaseSession.value<string> 1 row
            | [ _ ] -> return raise (OperatorFaultException OperatorFault.FormatMismatch)
            | _ -> return raise (InstallationConfigurationException())
        }

    let zone (value: string) =
        let eligible =
            value = "Etc/UTC"
            || TimeZoneInfo.GetSystemTimeZones() |> Seq.exists (fun zone -> zone.Id = value)

        if not eligible || value = "UTC" then
            raise (OperatorFaultException OperatorFault.TimeZoneInvalid)

        let resolved = TimeZoneInfo.FindSystemTimeZoneById value

        if not resolved.HasIanaId || resolved.Id <> value then
            raise (OperatorFaultException OperatorFault.TimeZoneInvalid)

        value

    let require connection token =
        task {
            do! requireIdentity connection token
            let! id, timeZone = installation connection token

            try
                zone timeZone |> ignore
            with
            | :? ArgumentException
            | :? OperatorFaultException -> raise (InstallationConfigurationException())

            if id = Guid.Empty then
                raise (InstallationConfigurationException())

            return id, timeZone
        }

    /// Assert the admitted tuple in the transaction that will access installation data.
    let requireExpected (expected: Installation) connection token =
        task {
            let! id, zone = require connection token

            if id <> expected.InstallationId || zone <> expected.BusinessTimeZone then
                raise (InstallationConfigurationException())
        }

    let private wrappedKey lockRow connection installationId token =
        task {
            let query =
                "SELECT wrapped_data_key FROM claimcore.installation WHERE singleton AND installation_id=$1"
                + (if lockRow then " FOR UPDATE" else "")

            let! rows = DatabaseSession.rows connection query [| box installationId |] token

            match rows with
            | [ row ] -> return DatabaseSession.value<byte array> 0 row
            | _ -> return raise (OperatorFaultException OperatorFault.DataKeyInvalid)
        }

    let loadCipher connection installationId masterKey token =
        task {
            let! wrapped = wrappedKey false connection installationId token
            return InstallationCipher.unwrap masterKey installationId wrapped
        }

    let rotate (source: NpgsqlDataSource) masterKey newMasterKey token =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! installationId, _ = require connection token
                    let! wrapped = wrappedKey true connection installationId token
                    use cipher = InstallationCipher.unwrap masterKey installationId wrapped

                    let! _ =
                        DatabaseSession.execute
                            connection
                            "UPDATE claimcore.installation SET wrapped_data_key=$1 WHERE singleton AND installation_id=$2"
                            [| box (cipher.Wrap newMasterKey); box installationId |]
                            token

                    do! DatabaseSession.commit transaction token
                    return installationId
                })

    let private install connection timeZone id wrapped token =
        task {
            do! apply connection token

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.installation(installation_id,business_time_zone,installation_format,wrapped_data_key) VALUES ($1,$2,$3,$4)"
                    [| box id; box timeZone; box (int16 InstallationFormat.Current); box wrapped |]
                    token

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.disclosure_retention(months) VALUES ($1)"
                    [| box DisclosureTrail.retentionMonths |]
                    token

            return ()
        }

    let initializeWith
        (source: NpgsqlDataSource)
        masterKey
        timeZone
        initializeOwner
        initialized
        existing
        token
        =
        task {
            let timeZone = zone timeZone

            return!
                DatabaseSession.transaction
                    source
                    Data.IsolationLevel.ReadCommitted
                    token
                    (fun connection transaction ->
                        task {
                            do! OwnerAdmission.require connection token
                            // CLAIMCOR: serialize namespace discovery and installation, even before tables exist.
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    "SELECT pg_advisory_xact_lock(4849322681855856466)"
                                    [||]
                                    token

                            let! present = exists connection token

                            if present then
                                let! installation = require connection token

                                use! _admitted =
                                    loadCipher connection (fst installation) masterKey token

                                return! existing connection installation token
                            else
                                let id = Guid.NewGuid()
                                use cipher = InstallationCipher.create id
                                do! install connection timeZone id (cipher.Wrap masterKey) token
                                do! initializeOwner connection id cipher
                                do! DatabaseSession.commit transaction token
                                return initialized id
                        })
        }

    let refuseExisting _ _ _ =
        raise (OperatorFaultException OperatorFault.AlreadyInitialized)

    let initialize source masterKey timeZone initializeOwner token =
        initializeWith source masterKey timeZone initializeOwner id refuseExisting token

/// Owner-only schema initialization; no upgrade, adoption or conversion is provided.
module internal SchemaAdministration =
    let initialize source masterKey timeZone initializeOwner cancellationToken =
        Baseline.initialize source masterKey timeZone initializeOwner cancellationToken
