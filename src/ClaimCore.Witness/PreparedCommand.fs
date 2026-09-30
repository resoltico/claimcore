namespace ClaimCore.Witness

open System.Threading
open System.Threading.Tasks
open Npgsql

/// Statements every checkout repeats are planned once per physical connection.
///
/// Npgsql keeps a connection's prepared statements across a pooled reset, so the first checkout of a
/// physical connection pays one extra round trip and every later checkout of it saves the server's
/// parse and plan. Parameters must be typed before preparing.
module internal PreparedCommand =
    let prepared (command: NpgsqlCommand) =
        command.Prepare()
        command

    let preparedAsync
        (command: NpgsqlCommand)
        (cancellationToken: CancellationToken)
        : Task<NpgsqlCommand> =
        task {
            do! command.PrepareAsync(cancellationToken)
            return command
        }

    let create (connection: NpgsqlConnection) (sql: string) =
        prepared (new NpgsqlCommand(sql, connection))

    let createAsync
        (connection: NpgsqlConnection)
        (sql: string)
        (cancellationToken: CancellationToken)
        =
        preparedAsync (new NpgsqlCommand(sql, connection)) cancellationToken
