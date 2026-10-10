namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

/// A session lease spans primary COMMIT and independent witness settlement. Audits take
/// the exclusive counterpart before any row lock so a stable proof drains both phases.
module internal AuthorityOperationFence =
    let private lockNamespace = 0x4343
    let private key = 0x41554454

    let private command connection name =
        let value = new NpgsqlCommand("SELECT " + name + "(@namespace, @key)", connection)

        value.Parameters.AddWithValue("namespace", lockNamespace) |> ignore
        value.Parameters.AddWithValue("key", key) |> ignore
        value

    let private discard (source: NpgsqlDataSource option) (connection: NpgsqlConnection) =
        match source with
        | Some owned -> owned.Clear()
        | None -> NpgsqlConnection.ClearPool(connection)

        connection.Close()

    let private release source (connection: NpgsqlConnection) shared =
        let name =
            if shared then
                "pg_advisory_unlock_shared"
            else
                "pg_advisory_unlock"

        try
            use command = command connection name

            if command.ExecuteScalar() <> box true then
                invalidOp "Authority operation lease was not held."
        with _ ->
            // A failed unlock cannot return a session with a retained lease to its pool.
            discard source connection
            // Retirement protects the pool without rewriting an already observed result.
            ()

    let private acquire
        source
        (connection: NpgsqlConnection)
        shared
        budget
        (ct: CancellationToken)
        =
        task {
            let name =
                if shared then
                    "pg_advisory_lock_shared"
                else
                    "pg_advisory_lock"

            use command = command connection name

            match budget with
            | Some seconds when seconds > 0 -> command.CommandTimeout <- seconds
            | Some _ -> invalidArg (nameof budget) "Authority acquisition budget must be finite."
            | None -> ()

            try
                let! _ = command.ExecuteNonQueryAsync(ct)
                ()
            with error ->
                // Cancellation may race the server granting a session-level lease.
                discard source connection
                raise error

            let mutable disposed = 0

            return
                { new IDisposable with
                    member _.Dispose() =
                        if Interlocked.Exchange(&disposed, 1) = 0 then
                            release source connection shared
                }
        }

    let acquireShared source connection ct = acquire source connection true None ct
    let acquireExclusive source connection ct = acquire source connection false None ct

    let acquireExclusiveWithin source connection seconds ct =
        acquire source connection false (Some seconds) ct
