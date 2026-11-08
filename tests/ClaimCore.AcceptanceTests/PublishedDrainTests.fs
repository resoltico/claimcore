module ClaimCore.AcceptanceTests.PublishedDrainTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
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

        let frame =
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
            |> nonNull

        let input = frame["input"] |> nonNull
        input["operationId"] <- JsonValue.Create operation
        (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create("DRAIN-" + operation)
        let bytes = Encoding.UTF8.GetBytes(frame.ToJsonString())
        let pending = Task.Run(fun () -> run fixture fixture.Environment bytes 0)

        do! waitForProvider observer
        fixture.RequestWebStop()
        do! Task.Delay 200

        Expect.isFalse
            fixture.WebHasExited
            "A stop signal cannot discard an admitted provider transaction"

        Expect.isFalse
            pending.IsCompleted
            "The client cannot receive fabricated final nonacceptance before the lock is released"

        do! transaction.CommitAsync()
        let! reply = pending.WaitAsync(TimeSpan.FromSeconds 20.)

        Expect.equal
            (outcomeKind reply)
            "ACCEPTED"
            "The admitted case write returns its committed receipt during graceful stop"

        let! exitCode = fixture.WaitForWebExit(TimeSpan.FromSeconds 10.)
        Expect.equal exitCode 0 "The owned process exits normally after admitted work drains"

        use count =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.ledger WHERE subject_kind='CASE' AND operation_id=$1",
                observer
            )

        count.Parameters.AddWithValue(Guid.Parse operation) |> ignore
        let! accepted = count.ExecuteScalarAsync()

        Expect.equal
            (unbox<int64> accepted)
            1L
            "Exactly one case acceptance remains after process exit"

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

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published graceful stop drains an admitted PostgreSQL case write"
        (async { do! qualify () |> Async.AwaitTask })
