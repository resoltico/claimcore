module ClaimCore.IntegrationTests.AdministrationTests

open ClaimCore.TestSupport

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Postgres
open Npgsql
open AdministrationProcess

let private kind (value: JsonElement) =
    value.GetProperty("kind").GetString() |> Option.ofObj |> Option.get

let private fault expected (reply: JsonElement) =
    Expect.equal (kind reply) "OPERATOR_FAULT" "Typed safe operator envelope"

    Expect.equal
        (reply.GetProperty("fault").GetString() |> nonNull)
        expected
        "Specific operator failure"

    Expect.equal
        (reply.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
        (set [ "kind"; "fault" ])
        "No raw arguments or provider details"

let private ownerArguments =
    [
        "--owner-issuer"
        "https://issuer.example/"
        "--owner-subject"
        "synthetic-owner"
        "--owner-name"
        "Administration fixture owner"
        "--owner-role"
        "CASE_HANDLER"
    ]

let private discovery binary connection =
    task {
        for _ in 1..2 do
            let! help = invoke binary (connection + ".absent") [ "help" ] 0

            Expect.equal
                (help.GetProperty("acts").EnumerateArray()
                 |> Seq.map (fun item -> item.GetString() |> nonNull)
                 |> Seq.toList)
                [
                    "serve"
                    "initialize"
                    "verify"
                    "rebuild"
                    "admit-restored-installation"
                    "head"
                    "status"
                    "maintenance"
                    "help"
                    "version"
                ]
                "Exact command set"

            let! _ = invoke binary (connection + ".absent") [ "version" ] 0

            let! refusal = invoke binary connection [ "unsupported.synthetic" ] 2
            fault "INVOCATION_INVALID" refusal
    }

let private initialize binary connection =
    task {
        let! missing = invoke binary connection [ "migrate" ] 2
        fault "INVOCATION_INVALID" missing
        let! _ = invoke binary connection [ "head" ] 2
        let! _ = invoke binary connection [ "verify" ] 2
        let! _ = invoke binary connection ([ "initialize"; "UTC" ] @ ownerArguments) 2

        let! first =
            invoke binary connection ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 0

        Expect.equal (kind first) "INITIALIZED" "Schema and owner are committed together"

        let! repeated =
            invoke binary connection ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 2

        fault "ALREADY_INITIALIZED" repeated
        let! status = invoke binary connection [ "status" ] 0

        Expect.equal
            (status
                .GetProperty("initialOwner")
                .GetProperty("displayName")
                .GetProperty("value")
                .GetString())
            "Administration fixture owner"
            "Status shows the recorded owner"

        let! retired = invoke binary connection [ "provision-owner" ] 2
        fault "INVOCATION_INVALID" retired
        return ()
    }

let private checkpoints binary connection =
    task {
        let! head = invoke binary connection [ "head" ] 0

        let position =
            JsonSerializer.SerializeToElement(
                {|
                    sequence = head.GetProperty("operationSequence").GetInt64()
                    entryHash = head.GetProperty("operationHash").GetString() |> nonNull
                |}
            )

        let checkpoint =
            privateText
                (Path.GetDirectoryName connection |> nonNull)
                "checkpoint.json"
                (head.GetRawText())

        for _ in 1..2 do
            let! verified = invoke binary connection [ "verify"; "--checkpoint"; checkpoint ] 0

            Expect.isTrue
                (JsonElement.DeepEquals(verified.GetProperty("position"), position))
                "Verifier agrees with head"

            let! repeated = invoke binary connection [ "head" ] 0
            Expect.equal (repeated.GetRawText()) (head.GetRawText()) "Read-only head repeats"

        let! status = invoke binary connection [ "status" ] 0
        Expect.equal (kind status) "STATUS" "Owner status has its own read-only result"

        Expect.isTrue
            (JsonElement.DeepEquals(status.GetProperty("position"), position))
            "Status reports the observed head without verification claims"

        let! required = invoke binary connection [ "rebuild"; "--checkpoint"; checkpoint ] 2

        fault "MAINTENANCE_REQUIRED" required

        let! maintenance = invoke binary connection [ "maintenance"; "on" ] 0

        Expect.equal
            (kind maintenance)
            "MAINTENANCE"
            "Owner maintenance is explicit before reconstruction"

        let! rebuilt = invoke binary connection [ "rebuild"; "--checkpoint"; checkpoint ] 0

        Expect.equal
            (kind rebuilt)
            "REBUILT"
            "The owner process exposes checkpoint-bound reconstruction"

        Expect.isTrue
            (JsonElement.DeepEquals(rebuilt.GetProperty("position"), position))
            "Owner rebuild preserves the head"

        let! resumed = invoke binary connection [ "maintenance"; "off" ] 0
        Expect.equal (kind resumed) "MAINTENANCE" "Off is explicit and generation-free"
        let! repeatedOff = invoke binary connection [ "maintenance"; "off" ] 0

        Expect.isFalse
            (repeatedOff.GetProperty("changed").GetBoolean())
            "Repeated off is idempotent"

        let! retiredPrune = invoke binary connection [ "prune-sessions" ] 2
        fault "INVOCATION_INVALID" retiredPrune

        let! requiredAdmission =
            invoke binary connection [ "admit-restored-installation"; "--checkpoint"; checkpoint ] 2

        fault "MAINTENANCE_REQUIRED" requiredAdmission

        let! _ = invoke binary connection [ "maintenance"; "on" ] 0

        let! admitted =
            invoke binary connection [ "admit-restored-installation"; "--checkpoint"; checkpoint ] 0

        Expect.equal
            (kind admitted)
            "RESTORED_INSTALLATION_ADMITTED"
            "Restore admission consumes trusted installation evidence"

        Expect.isTrue
            (JsonElement.DeepEquals(admitted.GetProperty("checkpoint"), head))
            "Admission preserves the trusted root and frontier"

        let! _ = invoke binary connection [ "maintenance"; "off" ] 0
        let! retired = invoke binary connection [ "invalidate-restored-sessions" ] 2
        fault "INVOCATION_INVALID" retired

        let! _ = invoke binary connection [ "rebuild" ] 64

        for changed in
            [
                "01:" + String.replicate 64 "0"
                "-1:" + String.replicate 64 "0"
                "1:NOT_A_HASH"
            ] do
            let! _ = invoke binary connection [ "verify"; "--expect"; changed ] 64
            ()
    }

let private divergence binary connection (database: NpgsqlConnection) =
    task {
        let! _ =
            DatabaseSession.execute
                database
                "UPDATE claimcore.ledger_head SET entry_hash=decode(repeat('ab',32),'hex')"
                [||]
                CancellationToken.None

        let! result = invoke binary connection [ "verify" ] 5
        Expect.equal (kind result) "DIVERGED" "Divergence has its own public kind and exit"
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

        let! head = invoke binary connection [ "head" ] 0
        let changed = System.Text.Json.Nodes.JsonNode.Parse(head.GetRawText()) |> nonNull
        changed["operationHash"] <- System.Text.Json.Nodes.JsonValue.Create(String.replicate 64 "0")

        let checkpoint =
            privateText
                (Path.GetDirectoryName connection |> nonNull)
                "mismatched-checkpoint.json"
                (changed.ToJsonString())

        let! mismatch = invoke binary connection [ "verify"; "--checkpoint"; checkpoint ] 5

        Expect.equal
            (mismatch.GetProperty("finding").GetString() |> nonNull)
            "EXPECTED_POSITION"
            "A structurally valid checkpoint must match accepted history"

        return ()
    }

let private connectionGuards
    binary
    directory
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    =
    task {
        let settings = NpgsqlConnectionStringBuilder(database.GetConnectionString())
        settings.Password <- "incorrect-synthetic-password"
        let path = privateText directory "bad-connection" settings.ConnectionString
        let! failed = invoke binary path [ "head" ] 2
        fault "CONNECTION_REJECTED" failed
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.GroupWrite)
        let! _ = invoke binary path [ "head" ] 2
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        settings.Host <- "external.invalid"
        settings.SslMode <- SslMode.Prefer
        File.WriteAllText(path, settings.ConnectionString)
        let! _ = invoke binary path ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 2
        File.WriteAllBytes(path, [| 0xffuy |])
        let! _ = invoke binary path [ "head" ] 2
        ()
    }

let private lostCommit
    binary
    directory
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    (connection: NpgsqlConnection)
    =
    task {
        do! DatabaseScenarios.resetForInitialization connection

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

        let! unknown =
            invoke binary proxyFile ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 4

        Expect.equal
            (kind unknown)
            "OUTCOME_UNKNOWN"
            "Landed commit with lost confirmation is uncertain"

        let directFile =
            privateText directory "direct-connection" (database.GetConnectionString())

        let! _ = invoke binary directFile [ "status" ] 0

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

let private foreignRefusal binary connectionFile (connection: NpgsqlConnection) =
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
                ([ "initialize"; "Etc/UTC" ] @ ownerArguments)
                [ "migrate" ]
                [ "provision-owner" ]
                [ "verify" ]
                [ "head" ]
            ] do
            let! _ = invoke binary connectionFile arguments 2 |> Async.AwaitTask
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
        let published = Path.Combine(directory, "published")

        let! outcome =
            async {
                try
                    // Check actual --no-build Release inputs before constructing a database fixture.
                    let! binary = publish published |> Async.AwaitTask
                    let database = PostgresFixture.empty ()

                    let! exercised =
                        async {
                            try
                                do! database.StartAsync() |> Async.AwaitTask
                                use source = NpgsqlDataSource.Create(database.GetConnectionString())

                                use! connection =
                                    source.OpenConnectionAsync().AsTask() |> Async.AwaitTask

                                let! _ =
                                    DatabaseSession.execute
                                        connection
                                        "CREATE ROLE claimcore_runtime LOGIN"
                                        [||]
                                        CancellationToken.None
                                    |> Async.AwaitTask

                                let connectionFile =
                                    privateText
                                        directory
                                        "connection"
                                        (database.GetConnectionString())

                                do! discovery binary connectionFile |> Async.AwaitTask

                                do! initialize binary connectionFile |> Async.AwaitTask


                                do! checkpoints binary connectionFile |> Async.AwaitTask

                                do! divergence binary connectionFile connection |> Async.AwaitTask

                                do! connectionGuards binary directory database |> Async.AwaitTask

                                do!
                                    lostCommit binary directory database connection
                                    |> Async.AwaitTask

                                do! foreignRefusal binary connectionFile connection
                                return Ok()
                            with error ->
                                return Error error
                        }

                    let dispose =
                        [ fun () -> database.DisposeAsync().AsTask().GetAwaiter().GetResult() ]

                    match exercised with
                    | Ok() -> FixtureDisposal.finish dispose
                    | Error error -> FixtureDisposal.afterFailure error dispose

                    return Ok()
                with error ->
                    return Error error
            }

        let removeGenerated () =
            if Directory.Exists published then
                if DirectoryInfo(published).LinkTarget <> null then
                    invalidOp "Owned publish cleanup must not follow a redirected directory."

                Directory.Delete(published, true)

        let retained () =
            Console.Error.WriteLine(
                "Synthetic owner-process evidence retained privately: " + directory
            )

        match outcome with
        | Ok() ->
            try
                removeGenerated ()
                Directory.Delete(directory, true)
            with error ->
                FixtureDisposal.afterFailure error [ retained ]
        | Error error ->
            // Run both steps even if deleting a generated copy fails; preserve every error.
            FixtureDisposal.afterFailure error [ removeGenerated; retained ]
    }

let private failedPublish () =
    async {
        let directory = Directory.CreateTempSubdirectory("claimcore-publish-test-").FullName

        try
            let blocked = Path.Combine(directory, "blocked-output")
            File.WriteAllText(blocked, "Synthetic regular file, not a publish directory.")

            let! failed =
                task {
                    try
                        let! _ = publish blocked
                        return false
                    with _ ->
                        return true
                }
                |> Async.AwaitTask

            Expect.isTrue
                failed
                "An actual invalid publish destination refuses before any database setup"

            let paths =
                [
                    Path.Combine(directory, "publish.stdout")
                    Path.Combine(directory, "publish.stderr")
                ]

            for path in paths do
                Expect.isTrue (File.Exists path) "Both bounded process streams are retained"

                Expect.isLessThanOrEqual
                    (FileInfo(path).Length)
                    1_048_576L
                    "Publish capture retains the existing process bound"

                Expect.equal
                    (File.GetUnixFileMode path)
                    (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    "Owner publish diagnostics remain private"

            Expect.isGreaterThan
                (paths |> List.sumBy (fun path -> FileInfo(path).Length))
                0L
                "The actual SDK failure has retained diagnostic output"
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
            [ "rebuild" ]
            [ "rebuild"; "--expect"; "1:" + String.replicate 64 "A" ]
            [ "invalidate-restored-sessions"; "extra" ]
            [ "head"; "extra" ]
            [ "status"; "extra" ]
            [ "maintenance"; "resume"; "--generation"; "0" ]
            [ "maintenance"; "resume"; "--generation"; "01" ]
            [ "prune-sessions"; "--limit"; "1001" ]
            [ "verify"; "--expect"; "9007199254740992:" + String.replicate 64 "0" ]
        ] do
        Expect.isError
            (ClaimCore.Server.Administration.Inputs.command arguments)
            "Exact arguments are refused"

    let directory = PrivateTestPaths.newPrivateDirectory "claimcore-checkpoint-input-"

    try
        let checkpoint =
            privateText
                directory
                "checkpoint.json"
                """{"installationId":"10000000-0000-4000-8000-000000000001","operationSequence":0,"operationHash":"0000000000000000000000000000000000000000000000000000000000000000","expiredBefore":null}"""

        Expect.isOk
            (ClaimCore.Server.Administration.Inputs.command [ "verify"; "--checkpoint"; checkpoint ])
            "A valid private checkpoint is admitted"
    finally
        Directory.Delete(directory, true)

    use broken = new BrokenOutput()

    for code, uncertain, expected in [ 0, true, 4; 4, true, 4; 2, false, 3; 0, false, 3 ] do
        Expect.equal
            (ClaimCore.Server.Administration.Replies.write
                broken
                ([| 123uy; 125uy |], code, uncertain))
            expected
            "Output loss preserves mutation uncertainty"

    Expect.equal
        (AdministrationFailures.classify (AdministrationOutcomeUnknownException()))
        AdministrationFailure.OutcomeUnknown
        "Commit knowledge is typed"

    Expect.equal
        (AdministrationFailures.classify (TimeoutException()))
        (AdministrationFailure.Fault OperatorFault.Unavailable)
        "No dispatched commit is inferred"

    Expect.equal
        (AdministrationFailures.classify (InvalidOperationException()))
        (AdministrationFailure.Fault OperatorFault.InternalFailure)
        "An untyped implementation failure is not misreported as a known precondition"

[<Tests>]
let tests =
    testList
        "Published owner administration"
        [
            testCaseAsync
                "failed owner publication retains bounded private diagnostics"
                (failedPublish ())
            testCase
                "argument and delivery guards reject without weakening commit knowledge"
                inputAndDeliveryGuards
            testCaseAsync
                "all acts succeed refuse and repeat on a fresh owned installation"
                (publishedCommands ())
        ]
