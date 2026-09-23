namespace ClaimCore.Postgres

open System.Data
open Npgsql

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

    let private create (connection: NpgsqlConnection) transaction requested =
        let baseline = SchemaDefinition.current ()
        use command = new NpgsqlCommand(baseline.Script, connection, transaction)
        command.ExecuteNonQuery() |> ignore

        use lineage =
            new NpgsqlCommand(
                "INSERT INTO claimcore.installation_lineage (business_time_zone) VALUES (@zone)",
                connection,
                transaction
            )

        Sql.text lineage "zone" requested

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

    let initialize connectionString zoneId =
        let requested =
            try
                Ok(InstallationBusinessZone.requireValid zoneId)
            with AdministrationException reason ->
                Error reason

        match requested with
        | Ok requested ->
            execute connectionString false (fun connection transaction ->
                match SchemaAdmission.inspect connection with
                | SchemaAdmissionState.Absent -> create connection transaction requested
                | state -> requireSupported state

                requireCurrent connection

                if InstallationBusinessZone.read connection <> requested then
                    AdministrationFailures.refuse
                        AdministrationFailure.BusinessZoneAlreadyConfigured)
        | Error reason -> AdministrationOutcome.NotStarted reason

    /// Read-only inspection for reconciliation, not an automatic retry or a repair operation.
    let verify connectionString =
        execute connectionString true (fun connection _ -> requireCurrent connection)
