module internal ClaimCore.IntegrationTests.DatabaseObservation

open System
open System.Diagnostics
open System.Threading
open Expecto
open Npgsql

let private waitUntilWithin budget message condition =
    let elapsed = Stopwatch.StartNew()
    let mutable found = condition ()

    while not found && elapsed.Elapsed < budget do
        Thread.Sleep 20
        found <- condition ()

    Expect.isTrue found message

let waitUntil message condition =
    waitUntilWithin (TimeSpan.FromSeconds 15.) message condition

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

    use clock = new NpgsqlCommand("SELECT clock_timestamp()", connection)
    let now = DateTimeOffset(clock.ExecuteScalar() :?> DateTime)
    let remaining = max TimeSpan.Zero (deadline - now)
    Expect.isLessThanOrEqual remaining (TimeSpan.FromMinutes 1.) "Synthetic expiry stays bounded."
    let budget = remaining + TimeSpan.FromSeconds 15.

    waitUntilWithin budget "Independent database time reaches the deadline." (fun () ->
        command.ExecuteScalar() :?> bool)
