namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Owner-only configuration for the single business calendar used by one ClaimCore installation.
/// The persisted name is a canonical runtime zone ID, never a host-local default or an alias.
module InstallationBusinessZone =
    let private invalidZone () =
        invalidArg
            "zoneId"
            "The business time zone must be a canonical IANA zone ID available to this runtime."

    let private canonical (zoneId: string) =
        let eligible =
            String.Equals(zoneId, "Etc/UTC", StringComparison.Ordinal)
            || TimeZoneInfo.GetSystemTimeZones()
               |> Seq.exists (fun zone -> String.Equals(zone.Id, zoneId, StringComparison.Ordinal))

        if String.IsNullOrWhiteSpace(zoneId) || zoneId <> zoneId.Trim() || not eligible then
            Error()
        else
            try
                let resolved = TimeZoneInfo.FindSystemTimeZoneById(zoneId)

                // HasIanaId keeps the stored calendar portable. A host can resolve and round-trip
                // its own platform identifier - a Windows zone name does so on Windows - but that
                // value would not resolve on the macOS and Linux hosts this runtime supports.
                if
                    resolved.HasIanaId
                    && String.Equals(resolved.Id, zoneId, StringComparison.Ordinal)
                then
                    Ok zoneId
                else
                    Error()
            with
            | :? TimeZoneNotFoundException
            | :? InvalidTimeZoneException -> Error()

    let private validated zoneId =
        match canonical zoneId with
        | Ok value -> value
        | Error() -> invalidZone ()

    let private read (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command =
            new NpgsqlCommand(
                "SELECT business_time_zone FROM claimcore.installation_lineage WHERE singleton FOR UPDATE",
                connection,
                transaction
            )

        match command.ExecuteScalar() with
        | null -> raise (InvalidDataException("Installation lineage is missing."))
        | :? DBNull -> None
        | :? string as value -> Some value
        | _ -> raise (InvalidDataException("Installation business time zone has an invalid type."))

    /// Configure the business zone once. An exact repeat is idempotent; a different zone fails.
    let set (connectionString: string) (zoneId: string) =
        let requested = validated zoneId
        let builder = Migrations.ownerBuilder connectionString
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        DatabaseEnvironment.requireCompatible connection
        Migrations.requireOwnerIdentity connection
        Migrations.requireCurrent connection
        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
        Sql.lockKey connection transaction "claimcore:installation-business-time-zone"

        match read connection transaction with
        | Some existing when String.Equals(existing, requested, StringComparison.Ordinal) ->
            transaction.Commit()
        | Some _ ->
            raise (
                InvalidOperationException(
                    "The installation business time zone is already configured."
                )
            )
        | None ->
            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.installation_lineage "
                    + "SET business_time_zone = @zone "
                    + "WHERE singleton AND business_time_zone IS NULL",
                    connection,
                    transaction
                )

            Sql.text command "zone" requested

            if command.ExecuteNonQuery() <> 1 then
                raise (InvalidDataException("Installation business time zone was not configured."))

            transaction.Commit()

    /// Runtime admission uses this after schema and ACL checks. It intentionally rejects a fresh
    /// or upgraded installation until its owner chooses the calendar used for business decisions.
    let requireConfigured
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        : Task<string> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            use command =
                new NpgsqlCommand(
                    "SELECT business_time_zone FROM claimcore.installation_lineage WHERE singleton",
                    connection
                )

            let! value = command.ExecuteScalarAsync(cancellationToken)
            cancellationToken.ThrowIfCancellationRequested()

            match value with
            | null -> return raise (InvalidDataException("Installation lineage is missing."))
            | :? DBNull ->
                return
                    raise (
                        InvalidOperationException(
                            "The installation business time zone has not been configured."
                        )
                    )
            | :? string as zoneId ->
                try
                    return validated zoneId
                with :? ArgumentException ->
                    return
                        raise (
                            InvalidOperationException(
                                "The installation business time zone is not valid on this runtime."
                            )
                        )
            | _ ->
                return
                    raise (
                        InvalidDataException("Installation business time zone has an invalid type.")
                    )
        }
