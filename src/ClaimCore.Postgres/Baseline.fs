namespace ClaimCore.Postgres

open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Npgsql

exception UnsupportedPostgresVersion

/// One baseline for the migrator and EVERY database connection, including direct store callers.
module Baseline =
    let private settings =
        match
            Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream("ClaimCore.PostgresBaseline.json")
            |> Option.ofObj
        with
        | None -> raise (InvalidDataException("The embedded PostgreSQL baseline is missing."))
        | Some stream ->
            use source = stream
            use document = JsonDocument.Parse(source)
            let major = document.RootElement.GetProperty("major").GetInt32()
            let minor = document.RootElement.GetProperty("minimumMinor").GetInt32()

            let image =
                document.RootElement.GetProperty("containerImage").GetString()
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("The PostgreSQL container image is missing.")))

            if major < 10 || minor < 0 || minor > 9999 then
                raise (InvalidDataException("The PostgreSQL baseline is invalid."))

            let expectedPrefix = $"postgres:{major}.{minor}@sha256:"

            if
                not (image.StartsWith(expectedPrefix, System.StringComparison.Ordinal))
                || not (
                    Regex.IsMatch(
                        image.Substring(expectedPrefix.Length),
                        "^[0-9a-f]{64}$",
                        RegexOptions.CultureInvariant
                    )
                )
            then
                raise (
                    InvalidDataException(
                        "The PostgreSQL image must pin the selected release to a multi-platform SHA-256 digest."
                    )
                )

            major, minor, image

    let majorVersion, minimumMinorVersion, containerImage = settings
    let minimumServerVersion = majorVersion * 10000 + minimumMinorVersion
    let maximumServerVersionExclusive = (majorVersion + 1) * 10000
    let minimumVersion = $"{majorVersion}.{minimumMinorVersion}"

    let isSupported serverVersion =
        serverVersion >= minimumServerVersion
        && serverVersion < maximumServerVersionExclusive

    let requireCompatible (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection)

        if not (isSupported (command.ExecuteScalar() :?> int)) then
            raise UnsupportedPostgresVersion

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT current_setting('server_version_num')::integer",
                    connection
                )

            let! version = command.ExecuteScalarAsync(cancellationToken)

            if not (isSupported (version :?> int)) then
                return raise UnsupportedPostgresVersion
        }

    let requireCompatibleAsync (connection: NpgsqlConnection) =
        requireCompatibleAsyncWithCancellation connection CancellationToken.None
