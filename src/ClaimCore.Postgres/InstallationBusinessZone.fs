namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Validation of the immutable business calendar installed with one ClaimCore baseline.
/// The persisted name is a canonical runtime zone ID, never a host-local default or an alias.
module internal InstallationBusinessZone =
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

    let internal requireValid zoneId =
        match canonical zoneId with
        | Ok value -> value
        | Error() -> AdministrationFailures.refuse AdministrationFailure.BusinessZoneInvalid

    let internal read (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT business_time_zone FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        match command.ExecuteScalar() with
        | :? string as value -> requireValid value
        | _ -> AdministrationFailures.refuse AdministrationFailure.InstallationLineageMissing

    /// Runtime reads the zone installed atomically with the baseline, never a host-local default.
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
