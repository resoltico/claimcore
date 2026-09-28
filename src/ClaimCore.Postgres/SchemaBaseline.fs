namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Fresh installation only. Existing unsupported namespaces are never adopted, repaired or deleted.
module SchemaBaseline =
    let private requireSupported state =
        match state with
        | SchemaAdmissionState.Current -> ()
        | SchemaAdmissionState.Absent ->
            AdministrationFailures.refuse AdministrationFailure.BaselineMissing
        | SchemaAdmissionState.Unsupported ->
            AdministrationFailures.refuse AdministrationFailure.UnsupportedInstallation
        | SchemaAdmissionState.IdentityMismatch ->
            AdministrationFailures.refuse AdministrationFailure.BaselineIdentityMismatch

    let internal requireCurrent (connection: NpgsqlConnection) =
        SchemaAdmission.inspect connection |> requireSupported
        RuntimeSchema.requireCompatible connection
        InstallationBusinessZone.read connection |> ignore

    let private requireKeyCheck
        (makeCheck: Guid -> Guid -> Guid * byte array)
        installationId
        lineageId
        storedKeyId
        (storedCheck: byte array)
        =
        let keyId, keyCheck = makeCheck installationId lineageId

        if
            keyId = Guid.Empty
            || isNull (box keyCheck)
            || keyCheck.Length <> 32
            || keyId <> storedKeyId
            || not (
                CryptographicOperations.FixedTimeEquals(
                    ReadOnlySpan<byte>(keyCheck),
                    ReadOnlySpan<byte>(storedCheck)
                )
            )
        then
            AdministrationFailures.refuse AdministrationFailure.DatabaseConfigurationInvalid

    let private currentKeyCheck
        (connection: NpgsqlConnection)
        (makeCheck: Guid -> Guid -> Guid * byte array)
        =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,suppression_key_id,suppression_key_check "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            AdministrationFailures.refuse AdministrationFailure.InstallationLineageMissing

        requireKeyCheck
            makeCheck
            (reader.GetGuid(0))
            (reader.GetGuid(1))
            (reader.GetGuid(2))
            (reader.GetFieldValue<byte array>(3))

        if reader.Read() then
            AdministrationFailures.refuse AdministrationFailure.InstallationLineageMissing

    let private create
        (connection: NpgsqlConnection)
        transaction
        requested
        scope
        (makeCheck: Guid -> Guid -> Guid * byte array)
        =
        let baseline = SchemaDefinition.current ()
        use command = new NpgsqlCommand(baseline.Script, connection, transaction)
        command.ExecuteNonQuery() |> ignore

        let installationId = Guid.NewGuid()
        let lineageId = Guid.NewGuid()
        let keyId, keyCheck = makeCheck installationId lineageId

        if keyId = Guid.Empty || isNull (box keyCheck) || keyCheck.Length <> 32 then
            AdministrationFailures.refuse AdministrationFailure.DatabaseConfigurationInvalid

        use lineage =
            new NpgsqlCommand(
                "INSERT INTO claimcore.installation_lineage "
                + "(installation_id,lineage_id,business_time_zone,data_use_scope,data_use_phase,"
                + "suppression_key_id,suppression_key_check) "
                + "VALUES (@installation,@lineage,@zone,@scope,@phase,@keyId,@keyCheck)",
                connection,
                transaction
            )

        Sql.uuid lineage "installation" installationId
        Sql.uuid lineage "lineage" lineageId
        Sql.text lineage "zone" requested
        Sql.text lineage "scope" (InstallationUse.scopeToken scope)

        Sql.text
            lineage
            "phase"
            (if scope = InstallationUseScope.SyntheticOnly then
                 "ACTIVE"
             else
                 "BOOTSTRAP_NO_CASES")

        Sql.uuid lineage "keyId" keyId
        Sql.add lineage "keyCheck" NpgsqlDbType.Bytea (box keyCheck)

        if lineage.ExecuteNonQuery() <> 1 then
            AdministrationFailures.refuse AdministrationFailure.InstallationLineageMissing

        use marker =
            new NpgsqlCommand(
                "INSERT INTO claimcore.schema_baseline (baseline_id, script_sha256) VALUES (@id, @digest)",
                connection,
                transaction
            )

        Sql.text marker "id" baseline.Id
        Sql.text marker "digest" baseline.Digest

        if marker.ExecuteNonQuery() <> 1 then
            AdministrationFailures.refuse AdministrationFailure.BaselineMarkerWriteFailed

    let private execute connectionString readOnly action =
        AdministrationExecution.run (fun progress ->
            SchemaDefinition.current () |> ignore
            let builder = OwnerConnection.builder connectionString
            use connection = new NpgsqlConnection(builder.ConnectionString)
            connection.Open()
            DatabaseEnvironment.requireCompatible connection
            OwnerConnection.requireIdentity connection
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            use settings =
                new NpgsqlCommand(
                    (if readOnly then "SET TRANSACTION READ ONLY; " else "")
                    + "SET LOCAL search_path = pg_catalog",
                    connection,
                    transaction
                )

            settings.ExecuteNonQuery() |> ignore
            progress.BeginWork()
            Sql.lockKey connection transaction "claimcore:schema"
            OwnerConnection.requireIdentity connection
            action connection transaction
            progress.Commit((fun () -> transaction.Commit()), ()))

    let private initializeWithScope
        scope
        connectionString
        zoneId
        (makeCheck: Guid -> Guid -> Guid * byte array)
        =
        let requested =
            try
                if isNull (box makeCheck) then
                    AdministrationFailures.refuse AdministrationFailure.DatabaseConfigurationInvalid

                Ok(InstallationBusinessZone.requireValid zoneId)
            with AdministrationException reason ->
                Error reason

        match requested with
        | Ok requested ->
            execute connectionString false (fun connection transaction ->
                match SchemaAdmission.inspect connection with
                | SchemaAdmissionState.Absent ->
                    create connection transaction requested scope makeCheck
                | state -> requireSupported state

                requireCurrent connection
                currentKeyCheck connection makeCheck

                use scopeQuery =
                    new NpgsqlCommand(
                        "SELECT data_use_scope FROM claimcore.installation_lineage WHERE singleton",
                        connection
                    )

                let storedScope =
                    match scopeQuery.ExecuteScalar() with
                    | :? string as value -> value
                    | _ ->
                        AdministrationFailures.refuse
                            AdministrationFailure.DatabaseConfigurationInvalid

                if storedScope <> InstallationUse.scopeToken scope then
                    AdministrationFailures.refuse
                        AdministrationFailure.DatabaseConfigurationInvalid

                if InstallationBusinessZone.read connection <> requested then
                    AdministrationFailures.refuse
                        AdministrationFailure.BusinessZoneAlreadyConfigured)
        | Error reason -> AdministrationOutcome.NotStarted reason

    let initialize connectionString zoneId makeCheck =
        initializeWithScope InstallationUseScope.SyntheticOnly connectionString zoneId makeCheck

    /// Internal isolated-qualification seam. No product caller supplies this profile: the public
    /// owner command obtains only the source-pinned reviewed build profile below.
    let internal initializeRealDataWithReviewedProfile
        (profile: ReviewedDeploymentProfile)
        connectionString
        zoneId
        makeCheck
        =
        if
            isNull (box profile)
            || isNull (box profile.PublicationRootKey)
            || profile.PublicationRootKey.Length <> 32
            || obj.ReferenceEquals(profile.BackupHealthPolicySha256, null)
            || profile.BackupHealthPolicySha256.Length <> 64
            || not (
                profile.BackupHealthPolicySha256
                |> Seq.forall (fun c -> ('0' <= c && c <= '9') || ('a' <= c && c <= 'f'))
            )
        then
            AdministrationOutcome.NotStarted AdministrationFailure.DatabaseConfigurationInvalid
        else
            initializeWithScope InstallationUseScope.RealData connectionString zoneId makeCheck

    let initializeRealData connectionString zoneId makeCheck =
        match ReviewedDeploymentRoot.current () with
        | None ->
            AdministrationOutcome.NotStarted AdministrationFailure.DatabaseConfigurationInvalid
        | Some profile ->
            try
                initializeRealDataWithReviewedProfile profile connectionString zoneId makeCheck
            finally
                CryptographicOperations.ZeroMemory(profile.PublicationRootKey)

    /// Read-only inspection for reconciliation, not an automatic retry or a repair operation.
    let verify connectionString =
        execute connectionString true (fun connection _ -> requireCurrent connection)
