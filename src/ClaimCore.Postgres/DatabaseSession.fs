namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Json

open System
open System.Globalization
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Administration commit confirmation was lost; inspect current state before another command.
type AdministrationOutcomeUnknownException() =
    inherit
        Exception(
            "Administration commit confirmation was lost; inspect current state before another command."
        )

/// Raised only where persisted rows fail their format contract.
type internal RecordCorruptException(reason: string) =
    inherit Exception(reason)

/// Rows share an immutable column lookup for their result set and own their captured values.
type internal NamedRow private (ordinals: Dictionary<string, int>, values: obj array) =
    member _.TryValue name =
        match ordinals.TryGetValue name with
        | true, index -> Some values[index]
        | _ -> None

    static member Reader(reader: System.Data.Common.DbDataReader) =
        let ordinals = Dictionary<string, int>(reader.FieldCount, StringComparer.Ordinal)

        // Map.ofArray previously retained the last value for a duplicated column name.
        for index in 0 .. reader.FieldCount - 1 do
            ordinals[reader.GetName index] <- index

        fun () -> NamedRow(ordinals, Array.init reader.FieldCount reader.GetValue)

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

    /// Initialize once for each result set, then capture independent rows as the reader advances.
    let namedRow (reader: System.Data.Common.DbDataReader) = NamedRow.Reader reader

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
            let read = namedRow reader
            let rows = ResizeArray<NamedRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(token)

                if found then rows.Add(read ()) else reading <- false

            return List.ofSeq rows
        }

    let namedValue<'T> name (row: NamedRow) =
        match row.TryValue name with
        | Some(:? 'T as value) -> value
        | _ -> raise (RecordCorruptException "Invalid stored column type or name.")

    let namedOptional<'T> name (row: NamedRow) =
        match row.TryValue name with
        | Some value when Convert.IsDBNull value -> None
        | Some(:? 'T as value) -> Some value
        | _ -> raise (RecordCorruptException "Invalid stored column type or name.")

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

    let namedOptionalCalendarDate name row =
        namedOptional<string> name row |> Option.map parsedCalendarDate


    let private recordedText text =
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

    let recordedInstant index row = recordedText (value<string> index row)

    let namedRecordedInstant name row =
        recordedText (namedValue<string> name row)

    let transaction (source: NpgsqlDataSource) isolation token action =
        task {
            let! connection = source.OpenConnectionAsync(token)
            let mutable transaction: NpgsqlTransaction option = None

            let! outcome =
                task {
                    try
                        let! started = connection.BeginTransactionAsync(isolation, token)
                        transaction <- Some started
                        let! result = action connection started
                        return Ok result
                    with error ->
                        return
                            Error(
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture error
                            )
                }

            match transaction with
            | Some transaction -> do! FailureDiagnostics.cleanup transaction.DisposeAsync
            | None -> ()

            do! FailureDiagnostics.cleanup connection.DisposeAsync

            match outcome with
            | Ok result -> return result
            | Error error ->
                error.Throw()
                return Unchecked.defaultof<_>
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
