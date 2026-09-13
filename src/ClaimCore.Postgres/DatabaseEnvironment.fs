namespace ClaimCore.Postgres

open System.Threading
open System.Threading.Tasks
open System.Data.Common
open Npgsql

/// Every access requires the published UTF-8, primary-server and local-durability contract.
module internal DatabaseEnvironment =
    let private settings =
        "SELECT current_setting('server_encoding') = 'UTF8', current_setting('client_encoding') = 'UTF8', "
        + "current_setting('fsync')::boolean, current_setting('full_page_writes')::boolean, "
        + "current_setting('synchronous_commit') <> 'off', "
        + "NOT current_setting('transaction_read_only')::boolean, NOT pg_is_in_recovery()"

    let private require (reader: DbDataReader) =
        for index in 0..6 do
            if not (reader.GetBoolean(index)) then
                raise RuntimeDatabaseMismatch

    let requireCompatible (connection: NpgsqlConnection) =
        Baseline.requireCompatible connection
        use command = new NpgsqlCommand(settings, connection)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise RuntimeDatabaseMismatch

        require reader

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            do! Baseline.requireCompatibleAsyncWithCancellation connection cancellationToken
            use command = new NpgsqlCommand(settings, connection)
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! hasRow = reader.ReadAsync(cancellationToken)

            if not hasRow then
                return raise RuntimeDatabaseMismatch

            require reader
        }

    let requireCompatibleAsync (connection: NpgsqlConnection) =
        requireCompatibleAsyncWithCancellation connection CancellationToken.None
