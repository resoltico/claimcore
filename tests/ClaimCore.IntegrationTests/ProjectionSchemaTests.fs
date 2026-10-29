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
        ]

let private columns (source: NpgsqlDataSource) =
    task {
        use! connection = source.OpenConnectionAsync()

        use command =
            new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'claimcore' AND table_name = 'cases'",
                connection
            )

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
            let! actual = columns database.OwnerSource |> Async.AwaitTask

            Expect.equal
                actual
                expected
                "SQL has exactly the declared snapshot, identity and worklist columns"

            use! connection =
                database.OwnerSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask

            do!
                DatabaseSession.execute
                    connection
                    "ALTER TABLE claimcore.cases ADD COLUMN synthetic_unlisted text"
                    [||]
                    CancellationToken.None
                |> Async.AwaitTask
                |> Async.Ignore

            let! changed = columns database.OwnerSource |> Async.AwaitTask
            Expect.notEqual changed expected "An actual added SQL column is detected"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync "SQL snapshot column coverage rejects extras" (projection ())
