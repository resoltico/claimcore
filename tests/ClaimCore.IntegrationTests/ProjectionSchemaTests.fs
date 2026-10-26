module ClaimCore.IntegrationTests.ProjectionSchemaTests

open ClaimCore.TestSupport

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Postgres

let private column (key: string) =
    key
    |> Seq.collect (fun c ->
        if Char.IsAsciiLetterUpper c then
            [ '_'; Char.ToLowerInvariant c ]
        else
            [ c ])
    |> Array.ofSeq
    |> String

let private expected =
    Record.fields
    |> List.map (fun row -> column row.Key)
    |> Set.ofList
    |> Set.union (set [ "case_id"; "revision"; "state" ])

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
            Expect.equal actual expected "SQL has exactly the business fields and technical columns"

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
    testCaseAsync
        "[CC-DOM-001] SQL column coverage follows the record declaration and rejects extras"
        (projection ())
