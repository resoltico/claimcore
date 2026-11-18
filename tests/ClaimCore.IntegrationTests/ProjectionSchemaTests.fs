module ClaimCore.IntegrationTests.ProjectionSchemaTests

open ClaimCore.TestSupport

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification

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
            "received_on"
            "registered_on"
            "closed_on"
            "last_activity_on"
            "work_search_text"
            "responsible_actor"
            "follow_up"
            "deadline_due"
            "claimed_amount"
            "claimed_currency"
            "payable_amount"
            "payable_currency"
            "payment_review"
            "outcome"
        ]

let private inventories =
    [
        "cases", expected
        "notes", set [ "note_id"; "case_id"; "created_sequence"; "created_effect_index"; "text" ]
        "case_payments",
        set
            [
                "payment_id"
                "case_id"
                "accepting_sequence"
                "effect_index"
                "payment_date"
                "paid_amount"
                "currency"
                "direction"
                "reference"
                "payee_id"
                "removed"
            ]
        "case_payment_totals", set [ "case_id"; "currency"; "paid"; "returned" ]
        "case_parties",
        set
            [
                "party_id"
                "case_id"
                "revision"
                "created_sequence"
                "created_effect_index"
                "removed"
                "role"
                "name"
                "details"
            ]
        "case_links",
        set [ "link_id"; "source_case_id"; "target_case_id"; "revision"; "removed"; "note" ]
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
                "summary_changed_at"
                "summary_recorded_by"
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
                "kind"
                "done_on"
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
