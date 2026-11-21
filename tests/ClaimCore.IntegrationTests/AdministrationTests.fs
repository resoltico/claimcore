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
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

let private discovery binary connection keyFile =
    task {
        for _ in 1..2 do
            let! help = invoke binary (connection + ".absent") (keyFile + ".absent") [ "help" ] 0

            Expect.equal
                (help.GetProperty("acts").EnumerateArray()
                 |> Seq.map (fun item -> item.GetString() |> nonNull)
                 |> Seq.toList)
                [
                    "serve"
                    "initialize"
                    "recover-owner"
                    "rotate-master-key"
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

            let! version =
                invoke binary (connection + ".absent") (keyFile + ".absent") [ "version" ] 0

            let! json =
                invoke
                    binary
                    (connection + ".absent")
                    (keyFile + ".absent")
                    [ "version"; "--json" ]
                    0

            Expect.equal (json.GetRawText()) (version.GetRawText()) "Both version forms emit JSON"

            let! refusal = invoke binary connection keyFile [ "unsupported.synthetic" ] 2
            fault "INVOCATION_INVALID" refusal
    }

let private initialize binary connection keyFile =
    task {
        let! missing = invoke binary connection keyFile [ "migrate" ] 2
        fault "INVOCATION_INVALID" missing
        let! _ = invoke binary connection keyFile [ "head" ] 2
        let! _ = invoke binary connection keyFile [ "verify" ] 2

        let! _ =
            invoke binary connection keyFile ([ "initialize"; "UTC" ] @ ownerArguments) 2

        let skip = [ "initialize"; "Etc/UTC"; "--skip-existing" ] @ ownerArguments
        let! first = invoke binary connection keyFile skip 0

        Expect.equal (kind first) "INITIALIZED" "Schema and owner are committed together"

        let! repeated =
            invoke binary connection keyFile ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 2

        fault "ALREADY_INITIALIZED" repeated
        let! before = invoke binary connection keyFile [ "head" ] 0
        let! skipped = invoke binary connection keyFile skip 0
        Expect.equal (kind skipped) "INITIALIZATION_SKIPPED" "Readable installation is kept"

        Expect.equal
            (skipped.GetProperty("installationId").GetGuid())
            (first.GetProperty("installationId").GetGuid())
            "The original installation is identified"

        let! after = invoke binary connection keyFile [ "head" ] 0

        Expect.equal
            (after.GetRawText())
            (before.GetRawText())
            "The skip leaves the ledger unchanged"

        let! status = invoke binary connection keyFile [ "status" ] 0

        Expect.equal
            (status
                .GetProperty("initialOwner")
                .GetProperty("displayName")
                .GetProperty("value")
                .GetString())
            "Administration fixture owner"
            "Status shows the recorded owner"

        let! retired = invoke binary connection keyFile [ "provision-owner" ] 2
        fault "INVOCATION_INVALID" retired
        return ()
    }

let private checkpoints binary connection keyFile =
    task {
        let! head = invoke binary connection keyFile [ "head" ] 0

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
            let! verified =
                invoke binary connection keyFile [ "verify"; "--checkpoint"; checkpoint ] 0

            Expect.isTrue
                (JsonElement.DeepEquals(verified.GetProperty("position"), position))
                "Verifier agrees with head"

            let! repeated = invoke binary connection keyFile [ "head" ] 0
            Expect.equal (repeated.GetRawText()) (head.GetRawText()) "Read-only head repeats"

        let! status = invoke binary connection keyFile [ "status" ] 0
        Expect.equal (kind status) "STATUS" "Owner status has its own read-only result"

        Expect.isTrue
            (JsonElement.DeepEquals(status.GetProperty("position"), position))
            "Status reports the observed head without verification claims"

        let pause = "10000000-0000-4000-8000-000000000099"
        let replacement = "10000000-0000-4000-8000-000000000098"
        let fresh = "10000000-0000-4000-8000-000000000097"

        let checkpointArguments act generation pauseId =
            [
                act
                "--checkpoint"
                checkpoint
                "--pause-generation"
                generation
                "--pause-id"
                pauseId
            ]

        let maintenanceState generation pauseId (reply: JsonElement) =
            let state = reply.GetProperty("maintenance")

            Expect.equal
                (state.GetProperty("generation").GetInt64())
                generation
                "Exact published maintenance generation"

            let actual = state.GetProperty("pauseId")

            let actual =
                if actual.ValueKind = JsonValueKind.Null then
                    None
                else
                    Some(actual.GetString() |> nonNull)

            Expect.equal actual pauseId "Exact current pause ownership"

        maintenanceState 0L None status

        for act in [ "rebuild"; "admit-restored-installation" ] do
            let! missing =
                invoke binary connection keyFile [ act; "--checkpoint"; checkpoint ] 64

            Expect.equal (kind missing) "USAGE_ERROR" "An omitted handle is a usage failure"

            let! inactive =
                invoke binary connection keyFile (checkpointArguments act "1" pause) 2

            fault "MAINTENANCE_REQUIRED" inactive

        let! acquired =
            invoke binary connection keyFile [ "maintenance"; "acquire"; "0"; pause ] 0

        Expect.equal (kind acquired) "MAINTENANCE" "Owner acquisition commits an explicit pause"

        Expect.isTrue
            (acquired.GetProperty("changed").GetBoolean())
            "Initial acquisition changes state"

        maintenanceState 1L (Some pause) acquired

        let! repeated =
            invoke binary connection keyFile [ "maintenance"; "acquire"; "0"; pause ] 0

        Expect.isFalse
            (repeated.GetProperty("changed").GetBoolean())
            "Only the same active acquisition is retryable"

        maintenanceState 1L (Some pause) repeated

        let! rebuilt =
            invoke binary connection keyFile (checkpointArguments "rebuild" "1" pause) 0

        Expect.equal (kind rebuilt) "REBUILT" "Reconstruction consumes the exact active handle"

        Expect.isTrue
            (JsonElement.DeepEquals(rebuilt.GetProperty("position"), position))
            "Owner rebuild preserves the head"

        let! takeover =
            invoke
                binary
                connection
                keyFile
                [ "maintenance"; "takeover"; "1"; pause; replacement ]
                0

        maintenanceState 2L (Some replacement) takeover

        Expect.isTrue
            (takeover.GetProperty("changed").GetBoolean())
            "Takeover transfers pause ownership"

        for act in [ "rebuild"; "admit-restored-installation" ] do
            let! stale = invoke binary connection keyFile (checkpointArguments act "1" pause) 2
            fault "MAINTENANCE_CONFLICT" stale

        let! staleResume =
            invoke binary connection keyFile [ "maintenance"; "resume"; "1"; pause ] 2

        fault "MAINTENANCE_CONFLICT" staleResume
        let! current = invoke binary connection keyFile [ "status" ] 0
        maintenanceState 2L (Some replacement) current

        let! admitted =
            invoke
                binary
                connection
                keyFile
                (checkpointArguments "admit-restored-installation" "2" replacement)
                0

        Expect.equal
            (kind admitted)
            "RESTORED_INSTALLATION_ADMITTED"
            "Restore admission consumes the exact current pause and trusted evidence"

        Expect.isTrue
            (JsonElement.DeepEquals(admitted.GetProperty("checkpoint"), head))
            "Admission preserves the trusted root and frontier"

        let! resumed =
            invoke binary connection keyFile [ "maintenance"; "resume"; "2"; replacement ] 0

        maintenanceState 3L None resumed

        Expect.isTrue
            (resumed.GetProperty("changed").GetBoolean())
            "An exact resume releases the pause"

        let! repeatedResume =
            invoke binary connection keyFile [ "maintenance"; "resume"; "2"; replacement ] 2

        fault "MAINTENANCE_CONFLICT" repeatedResume

        let! releasedAcquire =
            invoke binary connection keyFile [ "maintenance"; "acquire"; "0"; pause ] 2

        fault "MAINTENANCE_CONFLICT" releasedAcquire

        for act in [ "rebuild"; "admit-restored-installation" ] do
            let! inactive =
                invoke binary connection keyFile (checkpointArguments act "2" replacement) 2

            fault "MAINTENANCE_REQUIRED" inactive

        let! inactive = invoke binary connection keyFile [ "status" ] 0
        maintenanceState 3L None inactive

        let! reacquired =
            invoke binary connection keyFile [ "maintenance"; "acquire"; "3"; fresh ] 0

        maintenanceState 4L (Some fresh) reacquired

        let! released =
            invoke binary connection keyFile [ "maintenance"; "resume"; "4"; fresh ] 0

        maintenanceState 5L None released

        for retired in [ "prune-sessions"; "invalidate-restored-sessions" ] do
            let! refused = invoke binary connection keyFile [ retired ] 2
            fault "INVOCATION_INVALID" refused

        for retired in [ "on"; "off" ] do
            let! refused = invoke binary connection keyFile [ "maintenance"; retired ] 64

            Expect.equal
                (kind refused)
                "USAGE_ERROR"
                "Retired generation-free maintenance syntax is not accepted"

        let! _ = invoke binary connection keyFile [ "rebuild" ] 64

        for changed in
            [
                "01:" + String.replicate 64 "0"
                "-1:" + String.replicate 64 "0"
                "1:NOT_A_HASH"
            ] do
            let! _ = invoke binary connection keyFile [ "verify"; "--expect"; changed ] 64
            ()
    }

let private divergence binary connection keyFile (database: NpgsqlConnection) =
    task {
        let! _ =
            DatabaseSession.execute
                database
                "UPDATE claimcore.ledger_head SET entry_hash=decode(repeat('ab',32),'hex')"
                [||]
                CancellationToken.None

        let! result = invoke binary connection keyFile [ "verify" ] 5
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

        let! head = invoke binary connection keyFile [ "head" ] 0
        let changed = System.Text.Json.Nodes.JsonNode.Parse(head.GetRawText()) |> nonNull
        changed["operationHash"] <- System.Text.Json.Nodes.JsonValue.Create(String.replicate 64 "0")

        let checkpoint =
            privateText
                (Path.GetDirectoryName connection |> nonNull)
                "mismatched-checkpoint.json"
                (changed.ToJsonString())

        let! mismatch =
            invoke binary connection keyFile [ "verify"; "--checkpoint"; checkpoint ] 5

        Expect.equal
            (mismatch.GetProperty("finding").GetString() |> nonNull)
            "EXPECTED_POSITION"
            "A structurally valid checkpoint must match accepted history"

        return ()
    }

let private connectionGuards
    binary
    directory
    keyFile
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    =
    task {
        let settings =
            NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnection database)

        settings.Password <- "incorrect-synthetic-password"
        let path = privateText directory "bad-connection" settings.ConnectionString
        let! failed = invoke binary path keyFile [ "head" ] 2
        fault "CONNECTION_REJECTED" failed
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.GroupWrite)
        let! _ = invoke binary path keyFile [ "head" ] 2
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        settings.Host <- "external.invalid"
        settings.SslMode <- SslMode.Prefer
        File.WriteAllText(path, settings.ConnectionString)
        let! _ = invoke binary path keyFile ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 2
        File.WriteAllBytes(path, [| 0xffuy |])
        let! _ = invoke binary path keyFile [ "head" ] 2
        ()
    }

let private lostCommit
    binary
    directory
    keyFile
    (database: Testcontainers.PostgreSql.PostgreSqlContainer)
    (connection: NpgsqlConnection)
    =
    task {
        do! DatabaseScenarios.resetForInitialization connection

        let settings =
            NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnection database)

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
            invoke binary proxyFile keyFile ([ "initialize"; "Etc/UTC" ] @ ownerArguments) 4

        Expect.equal
            (kind unknown)
            "OUTCOME_UNKNOWN"
            "Landed commit with lost confirmation is uncertain"

        let directFile =
            privateText directory "direct-connection" (PostgresFixture.ownerConnection database)

        let! _ = invoke binary directFile keyFile [ "status" ] 0

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

let private foreignRefusal binary connectionFile keyFile (connection: NpgsqlConnection) =
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
            let! _ = invoke binary connectionFile keyFile arguments 2 |> Async.AwaitTask
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
        let keyFile = Path.Combine(directory, "data.key")
        PostgresFixture.writeKeyFile keyFile

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

                                use bootstrap =
                                    NpgsqlDataSource.Create(database.GetConnectionString())

                                do!
                                    PostgresFixture.createRoles bootstrap CancellationToken.None
                                    |> Async.AwaitTask

                                use source =
                                    NpgsqlDataSource.Create(
                                        PostgresFixture.ownerConnection database
                                    )

                                use! connection =
                                    source.OpenConnectionAsync().AsTask() |> Async.AwaitTask

                                let connectionFile =
                                    privateText
                                        directory
                                        "connection"
                                        (PostgresFixture.ownerConnection database)

                                do! discovery binary connectionFile keyFile |> Async.AwaitTask

                                do! initialize binary connectionFile keyFile |> Async.AwaitTask


                                do! checkpoints binary connectionFile keyFile |> Async.AwaitTask

                                do!
                                    divergence binary connectionFile keyFile connection
                                    |> Async.AwaitTask

                                do!
                                    connectionGuards binary directory keyFile database
                                    |> Async.AwaitTask

                                do!
                                    lostCommit binary directory keyFile database connection
                                    |> Async.AwaitTask

                                do! foreignRefusal binary connectionFile keyFile connection
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
