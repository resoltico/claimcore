module ClaimCore.IntegrationTests.NamedRowTests

open Expecto
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private corrupt action =
    try
        action ()
        failtest "Expected a stored column failure"
    with :? RecordCorruptException as error ->
        Expect.equal
            error.Message
            "Invalid stored column type or name."
            "Missing names, required nulls and wrong types retain the same error"

let private rows (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! captured =
            DatabaseSession.namedRows
                connection
                "SELECT n AS repeated,n+100 AS repeated,n::text AS \"Label\",n+10 AS label,'present'::text AS nullable,NULL::text AS nullable,NULL::integer AS optional,n AS optional,convert_to(n::text,'UTF8') AS bytes FROM generate_series(1,2) n ORDER BY n"
                [||]
                token

        Expect.equal captured.Length 2 "Both rows are retained after the reader is disposed"

        for number, row in List.indexed captured do
            let number = number + 1

            Expect.equal
                (DatabaseSession.namedValue<int> "repeated" row)
                (number + 100)
                "Last duplicate alias wins"

            Expect.equal
                (DatabaseSession.namedValue<string> "Label" row)
                (string number)
                "Names remain case sensitive"

            Expect.equal
                (DatabaseSession.namedValue<int> "label" row)
                (number + 10)
                "Case-distinct column retains its value"

            Expect.equal
                (DatabaseSession.namedOptional<string> "nullable" row)
                None
                "Last duplicate SQL null wins"

            Expect.equal
                (DatabaseSession.namedOptional<int> "repeated" row)
                (Some(number + 100))
                "Present optional values retain their exact type"

            Expect.equal
                (DatabaseSession.namedOptional<int> "optional" row)
                (Some number)
                "A nonnull last alias replaces an earlier SQL null"

            Expect.sequenceEqual
                (DatabaseSession.namedValue<byte array> "bytes" row)
                (System.Text.Encoding.UTF8.GetBytes(string number))
                "Captured binary values remain owned by their original row after reader disposal"

            corrupt (fun () -> DatabaseSession.namedValue<int> "missing" row |> ignore)
            corrupt (fun () -> DatabaseSession.namedOptional<int> "missing" row |> ignore)
            corrupt (fun () -> DatabaseSession.namedValue<string> "repeated" row |> ignore)
            corrupt (fun () -> DatabaseSession.namedOptional<string> "repeated" row |> ignore)
            corrupt (fun () -> DatabaseSession.namedValue<string> "nullable" row |> ignore)
    }

let private resultSets (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        use statement =
            DatabaseSession.statement
                connection
                "SELECT 7 AS value,'first'::text AS label; SELECT 'second'::text AS label,9 AS value"
                [||]

        use! reader = statement.ExecuteReaderAsync(token)
        let firstRow = DatabaseSession.namedRow reader
        let! firstFound = reader.ReadAsync(token)
        Expect.isTrue firstFound "First result set contains a row"
        let first = firstRow ()
        let! next = reader.NextResultAsync(token)
        Expect.isTrue next "Second result set is present"
        let secondRow = DatabaseSession.namedRow reader
        let! secondFound = reader.ReadAsync(token)
        Expect.isTrue secondFound "Second result set contains a row"
        let second = secondRow ()

        Expect.equal
            (DatabaseSession.namedValue<int> "value" first)
            7
            "Earlier row remains independent of the advanced reader"

        Expect.equal
            (DatabaseSession.namedValue<string> "label" first)
            "first"
            "Earlier schema remains bound to its row"

        Expect.equal
            (DatabaseSession.namedValue<int> "value" second)
            9
            "Second result set uses its own reordered schema"

        Expect.equal
            (DatabaseSession.namedValue<string> "label" second)
            "second"
            "Second schema resolves its own names"
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        "named database rows"
        [
            testCaseAsync "retain rows and exact column decoding semantics" (run rows)
            testCaseAsync "bind each result set to its own column ordinals" (run resultSets)
        ]
