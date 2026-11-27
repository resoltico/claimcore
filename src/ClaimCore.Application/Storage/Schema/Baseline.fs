namespace ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Globalization
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Domain.Tasks
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts

/// Exact source identity of the only supported installation baseline.
module internal Baseline =
    let identifier =
        "claimcore-format-"
        + InstallationFormat.Current.ToString(CultureInfo.InvariantCulture)

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

                return
                    {
                        InstallationId = DatabaseSession.value<Guid> 0 row
                        BusinessTimeZone = DatabaseSession.value<string> 1 row
                    }
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
            let! admitted = installation connection token

            try
                zone admitted.BusinessTimeZone |> ignore
            with
            | :? ArgumentException
            | :? OperatorFaultException -> raise (InstallationConfigurationException())

            if admitted.InstallationId = Guid.Empty then
                raise (InstallationConfigurationException())

            return admitted
        }

    /// Assert the admitted configuration in the transaction that will access installation data.
    let requireExpected (expected: Installation) connection token =
        task {
            let! actual = require connection token

            if actual <> expected then
                raise (InstallationConfigurationException())
        }

    let matchesBootstrap (installation: Installation) (record: RetainedOperation) =
        let root = record.Root

        root.Header.InstallationId = installation.InstallationId
        && root.Header.Sequence = Sequence.first
        && root.Header.PreviousHash = EntryHash.zero
        && root.Header.Authority = Authority.Operator
        && root.Structure.Task = (Task.declaration Registry.provisionOwner).Id

    /// Decode and authenticate the first public root without loading any private profile bytes.
    let requireBootstrap (installation: Installation) connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT operation_id FROM claimcore.ledger WHERE sequence=1"
                    [||]
                    token

            match rows with
            | [ row ] ->
                let operation = DatabaseSession.value<Guid> 0 row |> OperationId.create

                match operation with
                | Some operation ->
                    let! record =
                        OperationStorage.findStructure
                            connection
                            installation.InstallationId
                            operation
                            token

                    if not (Option.exists (matchesBootstrap installation) record) then
                        raise (InstallationConfigurationException())
                | None -> raise (InstallationConfigurationException())
            | _ -> raise (InstallationConfigurationException())
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

    let keyGenerations connection token =
        task {
            let! generations =
                DatabaseSession.rows
                    connection
                    "SELECT generation,active FROM claimcore.encryption_generations"
                    [||]
                    token

            match generations |> List.filter (DatabaseSession.value<bool> 1) with
            | [ row ] ->
                return
                    DatabaseSession.value<Guid> 0 row,
                    (generations |> List.map (DatabaseSession.value<Guid> 0) |> Set.ofList)
            | _ -> return raise (OperatorFaultException OperatorFault.DataKeyInvalid)
        }

    let loadCipher connection installationId masterKey token =
        task {
            let! active, admitted = keyGenerations connection token
            let! wrapped = wrappedKey false connection installationId token
            return InstallationCipher.unwrap masterKey installationId active admitted wrapped
        }

    /// Caller holds the common ledger head. Older generations stay admitted for decryption.
    let rotateEncryptionGeneration connection (reserved: Guid option) token =
        task {
            let! _ =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.encryption_generations SET active=false WHERE active"
                    [||]
                    token

            let! rows =
                DatabaseSession.rows
                    connection
                    """INSERT INTO claimcore.encryption_generations(generation,active)
                       VALUES (coalesce($1::uuid,gen_random_uuid()),true) RETURNING generation"""
                    [| reserved |> Option.map box |> Option.defaultValue (box DBNull.Value) |]
                    token

            return DatabaseSession.value<Guid> 0 (List.exactlyOne rows)
        }

    let currentEncryptionGeneration connection generation token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM claimcore.encryption_generations WHERE generation=$1 AND active)"
                    [| box generation |]
                    token

            return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
        }

    // The caller holds the ledger head lock. Recovery publications and both key rotations
    // use the same lock order.
    let advanceRecoveryPublication connection token =
        task {
            let! changed =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.register_maintenance SET publication_generation=publication_generation+1 WHERE singleton"
                    [||]
                    token

            if changed <> 1 then
                raise (RecordCorruptException "Register maintenance state is absent.")
        }

    let rotate (source: NpgsqlDataSource) masterKey newMasterKey token =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! installation = require connection token

                    let! _ =
                        DatabaseSession.rows
                            connection
                            "SELECT sequence FROM claimcore.lock_ledger_head()"
                            [||]
                            token

                    let! wrapped = wrappedKey true connection installation.InstallationId token

                    let! active, admitted = keyGenerations connection token

                    use cipher =
                        InstallationCipher.unwrap
                            masterKey
                            installation.InstallationId
                            active
                            admitted
                            wrapped

                    let! _ =
                        DatabaseSession.execute
                            connection
                            "UPDATE claimcore.installation SET wrapped_data_key=$1 WHERE singleton AND installation_id=$2"
                            [| box (cipher.Wrap newMasterKey); box installation.InstallationId |]
                            token

                    do! advanceRecoveryPublication connection token
                    do! DatabaseSession.commit transaction token
                    return installation.InstallationId
                })

    let private install
        connection
        (installation: Installation)
        (cipher: InstallationCipher)
        wrapped
        token
        =
        task {
            do! apply connection token

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.encryption_generations(generation,active,reserved) VALUES ($1,true,$2)"
                    [| box cipher.Generation; box (InstallationCipher.operationAllowance) |]
                    token


            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.installation(installation_id,business_time_zone,installation_format,wrapped_data_key) VALUES ($1,$2,$3,$4)"
                    [|
                        box installation.InstallationId
                        box installation.BusinessTimeZone
                        box (int16 InstallationFormat.Current)
                        box wrapped
                    |]
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
                                    loadCipher
                                        connection
                                        installation.InstallationId
                                        masterKey
                                        token

                                return! existing connection installation token
                            else
                                let installation =
                                    {
                                        InstallationId = Guid.NewGuid()
                                        BusinessTimeZone = timeZone
                                    }

                                use cipher = InstallationCipher.create installation.InstallationId

                                do!
                                    install
                                        connection
                                        installation
                                        cipher
                                        (cipher.Wrap masterKey)
                                        token

                                do! initializeOwner connection installation cipher
                                do! DatabaseSession.commit transaction token
                                return initialized installation
                        })
        }

    let refuseExisting _ _ _ =
        raise (OperatorFaultException OperatorFault.AlreadyInitialized)

/// Schema-only fixture construction. This creates no bootstrap evidence, so serving and
/// skip-existing admission refuse it until the fixture records the first owner.
module internal SchemaAdministration =
    let initialize source masterKey timeZone initializeOwner cancellationToken =
        Baseline.initializeWith
            source
            masterKey
            timeZone
            (fun connection (installation: Installation) cipher ->
                initializeOwner connection installation.InstallationId cipher)
            (fun (installation: Installation) -> installation.InstallationId)
            Baseline.refuseExisting
            cancellationToken
