namespace ClaimCore.Application.Storage

open System
open System.Globalization
open Npgsql

/// Live writes and rebuild COPY share the table columns and their native row encoding.
module internal ProjectionWrite =
    type Table =
        private
            {
                Name: string
                Columns: string
                Insert: string
            }

    let table name columns values conflict =
        {
            Name = name
            Columns = columns
            Insert = $"INSERT INTO claimcore.{name}({columns}) VALUES({values}) {conflict}"
        }

    let insert connection table parameters token =
        DatabaseSession.execute connection table.Insert parameters token

    let private copyField (value: objnull) =
        let text =
            match value with
            | null
            | :? DBNull -> None
            | :? string as text -> Some text
            | :? (byte array) as bytes -> Some("\\x" + Convert.ToHexStringLower bytes)
            | :? DateTimeOffset as instant ->
                Some(instant.ToString("O", CultureInfo.InvariantCulture))
            | :? DateTime as instant -> Some(instant.ToString("O", CultureInfo.InvariantCulture))
            | :? DateOnly as date -> Some(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            | :? bool as flag -> Some(if flag then "t" else "f")
            | :? IFormattable as scalar -> Some(scalar.ToString(null, CultureInfo.InvariantCulture))
            | _ -> invalidOp "Unsupported projection COPY value."

        match text with
        | None -> "\\N"
        | Some value ->
            value
                .Replace("\\", "\\\\")
                .Replace("\t", "\\t")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")

    let stage (connection: NpgsqlConnection) (table: Table) (rows: seq<objnull array>) token =
        task {
            let! _ =
                DatabaseSession.execute
                    connection
                    $"CREATE TEMP TABLE {table.Name} (LIKE claimcore.{table.Name}) ON COMMIT DROP"
                    [||]
                    token

            // The provider streams COPY. Only one row's encoded text is retained at a time.
            use! writer =
                connection.BeginTextImportAsync(
                    $"COPY pg_temp.{table.Name}({table.Columns}) FROM STDIN",
                    token
                )

            for row in rows do
                token.ThrowIfCancellationRequested()
                let encoded = row |> Seq.map copyField |> String.concat "\t"
                do! writer.WriteLineAsync(encoded.AsMemory(), token)
        }

    let clear connection table token =
        DatabaseSession.execute connection $"DELETE FROM claimcore.{table.Name}" [||] token

    let replace connection table token =
        DatabaseSession.execute
            connection
            $"INSERT INTO claimcore.{table.Name}({table.Columns}) SELECT {table.Columns} FROM pg_temp.{table.Name}"
            [||]
            token
