module ClaimCore.IntegrationTests.AdministrationTests

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Postgres
open Npgsql
open AdministrationProcess

let private tag (value: JsonElement) =
    value.GetProperty("tag").GetString() |> Option.ofObj |> Option.get

let private owner operationId subject =
    JsonSerializer.Serialize
        {|
            operationId = operationId
            issuer = "https://issuer.example/"
            subject = subject
        |}

let private discovery binary connection principal =
    task {
        for _ in 1..2 do
            let! help =
                invoke binary (connection + ".absent") (principal + ".absent") [ "help" ] 0

            Expect.equal
                (help.GetProperty("acts").EnumerateArray()
                 |> Seq.map (fun item -> item.GetString() |> nonNull)
                 |> Seq.toList)
                [ "initialize"; "provision-owner"; "verify"; "head"; "help"; "version" ]
                "Exact command set"

            let! _ =
                invoke binary (connection + ".absent") (principal + ".absent") [ "version" ] 0

            let! refusal = invoke binary connection principal [ "unsupported.synthetic" ] 2
            Expect.equal (tag refusal) "REFUSED" "Unsupported command"
    }

let private initialize binary connection principal =
    task {
        let! missing = invoke binary connection principal [ "migrate" ] 2
        Expect.equal (tag missing) "REFUSED" "Removed command refuses"
        let! _ = invoke binary connection principal [ "head" ] 2
        let! _ = invoke binary connection principal [ "verify" ] 2
        let! _ = invoke binary connection principal [ "initialize"; "UTC" ] 2
        let! first = invoke binary connection principal [ "initialize"; "Etc/UTC" ] 0
        let! repeated = invoke binary connection principal [ "initialize"; "Etc/UTC" ] 0

        Expect.equal
            (first.GetRawText())
            (repeated.GetRawText())
            "Initialize same zone repeats exactly"

        let! _ = invoke binary connection principal [ "initialize"; "Europe/Riga" ] 2
        return ()
    }

let private provision binary connection principal =
    task {
        let original = File.ReadAllText principal
        let extra = original.TrimEnd().TrimEnd('}') + ",\"unexpected\":true}"

        let duplicate =
            original.Replace("\"issuer\":", "\"issuer\":\"https://issuer.example/\",\"issuer\":")

        for malformed in
            [
                "{"
                "null"
                "{}"
                extra
                duplicate
                String.replicate 8193 " "
                owner Guid.Empty "owner"
            ] do
            File.WriteAllText(principal, malformed)
            let! refused = invoke binary connection principal [ "provision-owner" ] 2
            Expect.equal (tag refused) "REFUSED" "Invalid owner input is refused before bootstrap"

        File.WriteAllText(principal, original)
        let! first = invoke binary connection principal [ "provision-owner" ] 0
        Expect.equal (tag first) "ACCEPTED" "First human owner"
        let! repeated = invoke binary connection principal [ "provision-owner" ] 0
        Expect.equal (tag repeated) "ALREADY_ACCEPTED" "Exact repeat"

        Expect.equal
            (first.GetProperty("position").GetRawText())
            (repeated.GetProperty("position").GetRawText())
            "Repeat does not append"

        for malformed in
            [ owner (Guid.NewGuid()) "other"; original.Replace("synthetic-owner", "other") ] do
            File.WriteAllText(principal, malformed)
            let! refused = invoke binary connection principal [ "provision-owner" ] 2
            Expect.equal (tag refused) "REFUSED" "Invalid or replacement owner refused"

        File.WriteAllText(principal, original)
    }

let private checkpoints binary connection principal =
    task {
        let! head = invoke binary connection principal [ "head" ] 0
        let position = head.GetProperty("position")

        let expected =
            (position
                .GetProperty("sequence")
                .GetInt64()
                .ToString(Globalization.CultureInfo.InvariantCulture))
            + ":"
            + (position.GetProperty("hash").GetString() |> nonNull)

        for _ in 1..2 do
            let! verified =
                invoke binary connection principal [ "verify"; "--expect"; expected ] 0

            Expect.equal
                (verified.GetProperty("position").GetRawText())
                (position.GetRawText())
                "Verifier agrees with head"

            let! repeated = invoke binary connection principal [ "head" ] 0
            Expect.equal (repeated.GetRawText()) (head.GetRawText()) "Read-only head repeats"

        for changed in
            [
                "01:" + String.replicate 64 "0"
                "-1:" + String.replicate 64 "0"
                "1:NOT_A_HASH"
            ] do
            let! _ = invoke binary connection principal [ "verify"; "--expect"; changed ] 2
            ()
    }

let private divergence binary connection principal (database: NpgsqlConnection) =
    task {
        let! _ =
            DatabaseSession.execute
                database
                "UPDATE claimcore.ledger_head SET entry_hash=decode(repeat('ab',32),'hex')"
                [||]
                CancellationToken.None

        let! result = invoke binary connection principal [ "verify" ] 5
        Expect.equal (tag result) "DIVERGED" "Divergence has its own public tag and exit"
        Expect.equal (result.GetProperty("finding").GetString() |> nonNull) "HEAD" "Exact finding"

        Expect.equal
            (result.GetProperty("sequence").ValueKind)
            JsonValueKind.Null
            "Head divergence has no attributed ledger entry"

        let! _ =
            DatabaseSession.execute
                database
                "UPDATE claimcore.ledger_head SET entry_hash=(SELECT entry_hash FROM claimcore.ledger ORDER BY sequence DESC LIMIT 1)"
                [||]
                CancellationToken.None

        let! _ =
            invoke
                binary
                connection
                principal
                [ "verify"; "--expect"; "1:" + String.replicate 64 "0" ]
                5

        return ()
    }

let private connectionGuards
    binary
    directory
    principal
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    =
    task {
        let settings = NpgsqlConnectionStringBuilder(database.GetConnectionString())
        settings.Password <- "incorrect-synthetic-password"
        let path = privateText directory "bad-connection" settings.ConnectionString
        let! failed = invoke binary path principal [ "head" ] 3
        Expect.equal (tag failed) "FAILED" "Authentication failure reveals no provider detail"
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.GroupWrite)
        let! _ = invoke binary path principal [ "head" ] 2
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        settings.Host <- "external.invalid"
        settings.SslMode <- SslMode.Prefer
        File.WriteAllText(path, settings.ConnectionString)
        let! _ = invoke binary path principal [ "initialize"; "Etc/UTC" ] 2
        File.WriteAllBytes(path, [| 0xffuy |])
        let! _ = invoke binary path principal [ "head" ] 2
        ()
    }

let private lostCommit
    binary
    directory
    principal
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    (connection: NpgsqlConnection)
    =
    task {
        let! _ =
            DatabaseSession.execute
                connection
                "DROP SCHEMA claimcore CASCADE"
                [||]
                CancellationToken.None

        let settings = NpgsqlConnectionStringBuilder(database.GetConnectionString())

        use proxy =
            new CommitProxy.Proxy(
                nonNull settings.Host,
                settings.Port,
                CommitProxy.Drop.AfterCommit
            )

        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        settings.Pooling <- false
        let proxyFile = privateText directory "proxy-connection" settings.ConnectionString
        let! unknown = invoke binary proxyFile principal [ "initialize"; "Etc/UTC" ] 4

        Expect.equal
            (tag unknown)
            "OUTCOME_UNKNOWN"
            "Landed commit with lost confirmation is uncertain"

        let directFile =
            privateText directory "direct-connection" (database.GetConnectionString())

        let! _ = invoke binary directFile principal [ "initialize"; "Etc/UTC" ] 0

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.installation"
                [||]
                CancellationToken.None

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            1L
            "Safe repeat does not create another installation"
    }

let private foreignRefusal binary connectionFile principalFile (connection: NpgsqlConnection) =
    async {
        let! _ =
            DatabaseSession.execute
                connection
                "DROP SCHEMA claimcore CASCADE; CREATE SCHEMA claimcore; CREATE TABLE claimcore.foreign_marker(value int); INSERT INTO claimcore.foreign_marker VALUES(17)"
                [||]
                CancellationToken.None
            |> Async.AwaitTask

        for arguments in
            [
                [ "initialize"; "Etc/UTC" ]
                [ "migrate" ]
                [ "provision-owner" ]
                [ "verify" ]
                [ "head" ]
            ] do
            let! _ = invoke binary connectionFile principalFile arguments 2 |> Async.AwaitTask
            ()

        let! marker =
            DatabaseSession.rows
                connection
                "SELECT value FROM claimcore.foreign_marker"
                [||]
                CancellationToken.None
            |> Async.AwaitTask

        Expect.equal (DatabaseSession.value<int> 0 marker.Head) 17 "Foreign evidence untouched"
    }

let private publishedCommands () =
    async {
        let directory = Directory.CreateTempSubdirectory("claimcore-admin-test-").FullName
        let database = PostgresFixture.empty ()

        try
            do! database.StartAsync() |> Async.AwaitTask
            use source = NpgsqlDataSource.Create(database.GetConnectionString())
            use! connection = source.OpenConnectionAsync().AsTask() |> Async.AwaitTask

            let! _ =
                DatabaseSession.execute
                    connection
                    "CREATE ROLE claimcore_runtime LOGIN"
                    [||]
                    CancellationToken.None
                |> Async.AwaitTask

            let connectionFile =
                privateText directory "connection" (database.GetConnectionString())

            let principalFile =
                privateText directory "principal.json" (owner (Guid.NewGuid()) "synthetic-owner")

            let! binary = publish (Path.Combine(directory, "published")) |> Async.AwaitTask
            do! discovery binary connectionFile principalFile |> Async.AwaitTask
            do! initialize binary connectionFile principalFile |> Async.AwaitTask
            do! provision binary connectionFile principalFile |> Async.AwaitTask
            do! checkpoints binary connectionFile principalFile |> Async.AwaitTask
            do! divergence binary connectionFile principalFile connection |> Async.AwaitTask
            do! connectionGuards binary directory principalFile database |> Async.AwaitTask
            do! lostCommit binary directory principalFile database connection |> Async.AwaitTask

            do! foreignRefusal binary connectionFile principalFile connection
        finally
            try
                database.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                Directory.Delete(directory, true)
    }

type private BrokenOutput() =
    inherit MemoryStream()

    override _.Flush() =
        raise (IOException "Synthetic delivery loss")

let private inputAndDeliveryGuards () =
    for arguments in
        [
            [ "verify"; "--expect"; "01:" + String.replicate 64 "0" ]
            [ "verify"; "--expect"; "1:" + String.replicate 64 "A" ]
            [ "initialize" ]
            [ "head"; "extra" ]
            [ "verify"; "--expect"; "9007199254740992:" + String.replicate 64 "0" ]
        ] do
        Expect.isNone (ClaimCore.Database.Inputs.command arguments) "Exact arguments are refused"

    Expect.isSome
        (ClaimCore.Database.Inputs.command [ "verify"; "--expect"; "0:" + String.replicate 64 "0" ])
        "Canonical expected position"

    use broken = new BrokenOutput()

    for code, uncertain, expected in [ 0, true, 4; 4, true, 4; 2, false, 3; 0, false, 3 ] do
        Expect.equal
            (ClaimCore.Database.Replies.write broken ([| 123uy; 125uy |], code, uncertain))
            expected
            "Output loss preserves mutation uncertainty"

    Expect.equal
        (AdministrationFailures.classify (AdministrationOutcomeUnknownException()))
        AdministrationFailure.OutcomeUnknown
        "Commit knowledge is typed"

    Expect.equal
        (AdministrationFailures.classify (TimeoutException()))
        AdministrationFailure.Failed
        "No dispatched commit is inferred"

    Expect.equal
        (AdministrationFailures.classify (InvalidOperationException()))
        AdministrationFailure.Refused
        "Preconditions are refused"

[<Tests>]
let tests =
    testList
        "Published owner administration"
        [
            testCase
                "argument and delivery guards reject without weakening commit knowledge"
                inputAndDeliveryGuards
            testCaseAsync
                "[CC-DB-001][CC-DB-002] all acts succeed refuse and repeat on a fresh owned installation"
                (publishedCommands ())
        ]
