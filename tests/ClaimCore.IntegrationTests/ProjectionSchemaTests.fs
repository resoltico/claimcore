module ClaimCore.IntegrationTests.ProjectionSchemaTests

open ClaimCore.TestSupport

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Postgres

let private expected =
    set
        [
            "case_id"
            "case_reference"
            "revision"
            "state"
            "snapshot"
            "status"
            "progress"
            "changed_at"
            "search_text"
            "notified_on"
            "work_search_text"
            "responsible_actor"
            "follow_up"
            "claimed_amount"
            "claimed_currency"
            "payable_amount"
            "payable_currency"
        ]

let private inventories =
    [
        "cases", expected
        "case_work",
        set
            [
                "case_id"
                "revision"
                "snapshot"
                "responsible_actor"
                "handling_summary"
                "search_text"
                "changed_at"
                "recorded_by"
            ]
        "reminders",
        set
            [
                "reminder_id"
                "case_id"
                "revision"
                "snapshot"
                "state"
                "action"
                "due"
                "search_text"
                "changed_at"
                "recorded_by"
            ]
    ]

let private columns (source: NpgsqlDataSource) table =
    task {
        use! connection = source.OpenConnectionAsync()

        use command =
            new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'claimcore' AND table_name = @table_name",
                connection
            )

        command.Parameters.AddWithValue("table_name", table) |> ignore
        use! reader = command.ExecuteReaderAsync()
        let found = ResizeArray<string>()

        while reader.Read() do
            found.Add(reader.GetString 0)

        return Set.ofSeq found
    }

let private projection () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            for table, expected in inventories do
                let! actual = columns database.OwnerSource table |> Async.AwaitTask

                Expect.equal
                    actual
                    expected
                    "SQL has exactly the independently declared projection columns"

                use! connection =
                    database.OwnerSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask

                do!
                    DatabaseSession.execute
                        connection
                        ("ALTER TABLE claimcore." + table + " ADD COLUMN synthetic_unlisted text")
                        [||]
                        CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.Ignore

                let! changed = columns database.OwnerSource table |> Async.AwaitTask

                Expect.notEqual
                    changed
                    expected
                    "An actual added column is detected for each affected projection"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync "SQL snapshot column coverage rejects extras" (projection ())
