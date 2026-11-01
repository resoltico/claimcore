module ClaimCore.AcceptanceTests.PublishedDrainTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open Expecto
open CliCalls

let private waitForProvider (connection: NpgsqlConnection) =
    task {
        let watch = Stopwatch.StartNew()
        let mutable found = false

        while not found && watch.Elapsed < TimeSpan.FromSeconds 10. do
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE usename='claimcore_runtime' AND wait_event_type='Lock' AND query LIKE 'SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head%')",
                    connection
                )

            let! value = command.ExecuteScalarAsync()
            found <- unbox<bool> value

            if not found then
                do! Task.Delay 20

        Expect.isTrue
            found
            "The actual published provider is independently observed waiting for the owned head lock"
    }

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        setup fixture

        use source =
            NpgsqlDataSource.Create(
                File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))
            )

        use! connection = source.OpenConnectionAsync()
        use! observer = source.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        use head =
            new NpgsqlCommand(
                "SELECT sequence FROM claimcore.ledger_head WHERE singleton FOR UPDATE",
                connection,
                transaction
            )

        let! original = head.ExecuteScalarAsync()
        let before = unbox<int64> original
        let operation = id ()

        let pending =
            Task.Run(fun () ->
                verb fixture fixture.Environment [ "operation"; "settle"; operation ] 2)

        do! waitForProvider observer
        let watch = Stopwatch.StartNew()
        fixture.RequestWebStop()
        do! Task.Delay 200

        Expect.isFalse
            fixture.WebHasExited
            "A stop signal cannot discard an admitted provider transaction"

        Expect.isFalse
            pending.IsCompleted
            "The client cannot receive fabricated final nonacceptance before the lock is released"

        let observedHold = watch.Elapsed.TotalMilliseconds
        do! transaction.CommitAsync()
        let! reply = pending.WaitAsync(TimeSpan.FromSeconds 20.)

        Expect.equal
            (outcomeKind reply)
            "NOT_ACCEPTED"
            "The admitted final control returns its actual committed answer during graceful stop"

        let! exitCode = fixture.WaitForWebExit(TimeSpan.FromSeconds 10.)
        Expect.equal exitCode 0 "The owned process exits normally after admitted work drains"

        use count =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.ledger WHERE subject_kind='CONTROL' AND operation_id=$1",
                observer
            )

        count.Parameters.AddWithValue(Guid.Parse operation) |> ignore
        let! controls = count.ExecuteScalarAsync()

        Expect.equal
            (unbox<int64> controls)
            1L
            "Exactly one retained final control exists after process exit"

        use after =
            new NpgsqlCommand(
                "SELECT sequence FROM claimcore.ledger_head WHERE singleton",
                observer
            )

        let! position = after.ExecuteScalarAsync()

        Expect.equal
            (unbox<int64> position)
            (before + 1L)
            "Graceful stop neither loses nor duplicates the ordered append"

        match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
        | null -> ()
        | directory ->
            Directory.CreateDirectory directory |> ignore

            File.WriteAllBytes(
                Path.Combine(directory, "published-drain.json"),
                JsonSerializer.SerializeToUtf8Bytes
                    {|
                        provider = "postgresql"
                        signal = "SIGTERM"
                        minimumHoldMilliseconds = 200
                        observedProviderHoldMilliseconds = observedHold
                        exitCode = exitCode
                        retainedControls = 1
                        elapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                    |}
            )

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published graceful stop retains an admitted PostgreSQL control through delayed provider completion"
        (async { do! qualify () |> Async.AwaitTask })
