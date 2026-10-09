module internal ClaimCore.IntegrationTests.RestorePhysicalWalRange

open System
open System.Text.RegularExpressions
open Expecto
open Npgsql

let completedWalEndpoint connectionString =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand(
            // With no intervening writes, switch returns the following segment boundary.
            // The completed prefix ends exclusively, so its last file contains lsn - 1.
            "WITH tip AS MATERIALIZED (SELECT pg_switch_wal() AS lsn) "
            + "SELECT lsn::text,pg_walfile_name(lsn - 1) FROM tip",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic source WAL flush endpoint is unavailable"

    let position = reader.GetString(0)
    let segment = reader.GetString(1)

    if
        reader.Read()
        || not (Regex.IsMatch(position, "^[0-9A-F]{1,8}/[0-9A-F]{1,8}$"))
        || not (Regex.IsMatch(segment, "^[0-9A-F]{24}$"))
    then
        failtest "Synthetic source WAL flush identity is invalid"

    position, segment

let private walPosition (value: string) =
    let parts = value.Split('/')

    if parts.Length <> 2 then
        failtest "Synthetic WAL endpoint is malformed"

    (Convert.ToUInt64(parts[0], 16) <<< 32) ||| Convert.ToUInt64(parts[1], 16)

let requiredSegments baseEndLsn timeline segmentBytes horizon lastSegment =
    let start = walPosition baseEndLsn
    let ending = walPosition horizon
    let size = uint64 segmentBytes

    if size = 0UL || ending <= start then
        failtest "Synthetic completed WAL horizon does not follow BASE"

    let first = start / size
    let last = (ending - 1UL) / size
    let count = last - first + 1UL

    if count < 1UL || count > 1000UL then
        failtest "Synthetic completed WAL prefix exceeds reviewed bound"

    let segmentsPerLog = 0x100000000UL / size

    let names =
        [ 0UL .. count - 1UL ]
        |> List.map (fun offset ->
            let segment = first + offset
            $"{timeline:X8}{segment / segmentsPerLog:X8}{segment % segmentsPerLog:X8}")

    if List.last names <> lastSegment then
        failtest "Completed WAL segment does not match the exclusive horizon"

    String.Join(',', names)
