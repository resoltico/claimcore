namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Administration commit confirmation was lost. Repeating the same command is safe.
type AdministrationOutcomeUnknownException() =
    inherit Exception("Administration commit confirmation was lost; repeat the same command.")

module internal DatabaseSession =
    let command (connection: NpgsqlConnection) sql (parameters: objnull array) =
        let command = new NpgsqlCommand(sql, connection)

        for value in parameters do
            command.Parameters.Add(NpgsqlParameter(Value = value)) |> ignore

        command

    let execute connection sql parameters (token: CancellationToken) =
        task {
            use command = command connection sql parameters
            return! command.ExecuteNonQueryAsync(token)
        }

    let rows connection sql parameters (token: CancellationToken) =
        task {
            use command = command connection sql parameters
            use! reader = command.ExecuteReaderAsync(token)
            let rows = ResizeArray<obj array>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(token)

                if found then
                    let row = Array.zeroCreate<obj> reader.FieldCount
                    reader.GetValues(row) |> ignore
                    rows.Add row
                else
                    reading <- false

            return List.ofSeq rows
        }

    let value<'T> index (row: obj array) = unbox<'T> row[index]

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
