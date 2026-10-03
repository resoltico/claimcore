module internal ClaimCore.IntegrationTests.DatabaseObservation

open System
open System.Diagnostics
open System.Threading
open Expecto
open Npgsql

let waitUntil message condition =
    let elapsed = Stopwatch.StartNew()
    let mutable found = condition ()

    while not found && elapsed.Elapsed < TimeSpan.FromSeconds 15. do
        Thread.Sleep 20
        found <- condition ()

    Expect.isTrue found message

let blockedBy (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity a "
            + "WHERE pg_backend_pid()=ANY(pg_blocking_pids(a.pid)))",
            connection
        )

    use refresh = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", connection)

    // Activity is cached within the held transaction; a new backend must remain observable.
    waitUntil "PostgreSQL observes a waiter blocked by the held test backend." (fun () ->
        refresh.ExecuteNonQuery() |> ignore
        command.ExecuteScalar() :?> bool)

let lockWait connectionString event =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() "
            + "AND usename=current_user AND wait_event_type='Lock' AND wait_event=@event)",
            connection
        )

    command.Parameters.AddWithValue("event", event) |> ignore

    waitUntil "PostgreSQL observes the test role queued on the expected lock." (fun () ->
        command.ExecuteScalar() :?> bool)

let afterInstant (connection: NpgsqlConnection) (deadline: DateTimeOffset) =
    use command = new NpgsqlCommand("SELECT clock_timestamp() >= @deadline", connection)
    command.Parameters.AddWithValue("deadline", deadline) |> ignore

    waitUntil "Independent database time reaches the retention deadline." (fun () ->
        command.ExecuteScalar() :?> bool)
