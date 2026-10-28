namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Json

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Administration commit confirmation was lost. Repeating the same command is safe.
type AdministrationOutcomeUnknownException() =
    inherit Exception("Administration commit confirmation was lost; repeat the same command.")

/// Raised only where persisted rows fail their format contract.
type internal RecordCorruptException(reason: string) =
    inherit Exception(reason)

module internal DatabaseSession =
    let admitted create raw =
        create raw
        |> Option.defaultWith (fun () ->
            raise (RecordCorruptException "Invalid stored identifier or position."))

    let statement (connection: NpgsqlConnection) sql (parameters: objnull array) =
        let statement = new NpgsqlCommand(sql, connection)

        for value in parameters do
            statement.Parameters.Add(NpgsqlParameter(Value = value)) |> ignore

        statement

    let execute connection sql parameters (token: CancellationToken) =
        task {
            use statement = statement connection sql parameters
            return! statement.ExecuteNonQueryAsync(token)
        }

    let row (reader: NpgsqlDataReader) =
        Array.init reader.FieldCount reader.GetValue

    let namedRow (reader: NpgsqlDataReader) =
        Array.init reader.FieldCount (fun index -> reader.GetName index, reader.GetValue index)
        |> Map.ofArray

    let rows connection sql parameters (token: CancellationToken) =
        task {
            use statement = statement connection sql parameters
            use! reader = statement.ExecuteReaderAsync(token)
            let rows = ResizeArray<obj array>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(token)

                if found then rows.Add(row reader) else reading <- false

            return List.ofSeq rows
        }

    let namedRows connection sql parameters (token: CancellationToken) =
        task {
            use statement = statement connection sql parameters
            use! reader = statement.ExecuteReaderAsync(token)
            let rows = ResizeArray<Map<string, obj>>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(token)

                if found then
                    rows.Add(namedRow reader)
                else
                    reading <- false

            return List.ofSeq rows
        }

    let namedValue<'T> name (row: Map<string, obj>) =
        match Map.tryFind name row with
        | Some(:? 'T as value) -> value
        | _ -> raise (RecordCorruptException "Invalid stored column type or name.")

    let namedOptional<'T> name (row: Map<string, obj>) =
        match Map.tryFind name row with
        | Some value when Convert.IsDBNull value -> None
        | _ -> Some(namedValue<'T> name row)

    let value<'T> index (row: obj array) =
        match row[index] with
        | :? 'T as value -> value
        | _ -> raise (RecordCorruptException "Invalid stored column type.")

    let optional<'T> index (row: obj array) =
        if Convert.IsDBNull row[index] then
            None
        else
            Some(value<'T> index row)

    let nullValue = box DBNull.Value

    let parameter value =
        value |> Option.map box |> Option.defaultValue nullValue

    let instant index row =
        DateTimeOffset(value<DateTime> index row)

    // Explicit era and UTC formatting avoids server DateStyle and Npgsql infinity conversions.
    let dateExpression column =
        $"CASE WHEN {column} IS NULL THEN NULL WHEN NOT isfinite({column}) THEN 'invalid' ELSE to_char({column}, 'YYYY-MM-DD AD') END"

    let instantExpression column =
        $"CASE WHEN NOT isfinite({column}) THEN 'invalid' ELSE to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\" AD') END"

    let private parsedCalendarDate (text: string) =

        match
            DateOnly.TryParseExact(
                text,
                (CalendarDate.ExactFormat + " 'AD'"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
        with
        | true, parsed when
            parsed.ToString((CalendarDate.ExactFormat + " 'AD'"), CultureInfo.InvariantCulture) =
                text
            ->
            parsed
        | _ -> raise (RecordCorruptException "Invalid stored calendar date.")

    let calendarDate index row =
        parsedCalendarDate (value<string> index row)


    let recordedInstant index row =
        let text = value<string> index row
        let format = JsonFormats.InstantFormat + " 'AD'"

        match
            DateTimeOffset.TryParseExact(
                text,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed when parsed.ToString(format, CultureInfo.InvariantCulture) = text -> parsed
        | _ -> raise (RecordCorruptException "Invalid stored ledger instant.")

    let transaction (source: NpgsqlDataSource) isolation token action =
        task {
            use! connection = source.OpenConnectionAsync(token)
            use! transaction = connection.BeginTransactionAsync(isolation, token)
            return! action connection transaction
        }

    let read source token action =
        transaction source Data.IsolationLevel.RepeatableRead token (fun connection _ ->
            action connection)

    let commitUncertain (error: PostgresException) =
        error.InvariantSeverity = "FATAL"
        || error.InvariantSeverity = "PANIC"
        || error.SqlState.StartsWith("08", StringComparison.Ordinal)
        || error.SqlState = "40003"

    let commit (transaction: NpgsqlTransaction) (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()

            try
                do! transaction.CommitAsync(token)
            with
            | :? PostgresException as error when commitUncertain error ->
                return raise (AdministrationOutcomeUnknownException())
            | :? PostgresException as error -> return raise error
            | :? NpgsqlException
            | :? OperationCanceledException
            | :? TimeoutException
            | :? IO.IOException -> return raise (AdministrationOutcomeUnknownException())
        }
