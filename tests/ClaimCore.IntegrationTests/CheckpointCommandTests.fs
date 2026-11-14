module ClaimCore.IntegrationTests.CheckpointCommandTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Server.Administration
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private kind (value: JsonElement) =
    value.GetProperty("kind").GetString() |> nonNull

let private commandPath () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask
        let directory = PrivateTestPaths.newPrivateDirectory "claimcore-checkpoint-command-"
        let mutable completed = false

        try
            let fixture = database.Fixture
            let! _ = caseWorker fixture |> Async.AwaitTask

            let connection =
                AdministrationProcess.privateText
                    directory
                    "connection"
                    (database.Container.GetConnectionString())

            let configuration =
                typeof<DatabaseSuccess>
                    .Assembly.GetCustomAttributes(
                        typeof<System.Reflection.AssemblyConfigurationAttribute>,
                        false
                    )
                |> Array.exactlyOne
                |> unbox<System.Reflection.AssemblyConfigurationAttribute>
                |> _.Configuration

            let! binary =
                AdministrationProcess.publishConfiguration
                    configuration
                    (Path.Combine(directory, "published"))
                |> Async.AwaitTask

            let invoke arguments exit =
                AdministrationProcess.invoke binary connection arguments (ExitCode.value exit)
                |> Async.AwaitTask

            let! head = invoke [ "head" ] ExitCode.Success

            Expect.equal
                (head.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                (set [ "installationId"; "operationSequence"; "operationHash"; "expiredBefore" ])
                "Head directly emits the closed reusable artifact"

            let checkpoint =
                CheckpointRecord.decode (Encoding.UTF8.GetBytes(head.GetRawText()))
                |> Result.defaultWith (fun error -> failtestf "%A" error)

            let file =
                AdministrationProcess.privateText directory "checkpoint.json" (head.GetRawText())

            let! verified = invoke [ "verify"; "--checkpoint"; file ] ExitCode.Success

            Expect.equal
                (kind verified)
                "VERIFIED"
                "Published verifier accepts the emitted checkpoint"

            let now = DateTimeOffset.UtcNow.AddMinutes 1
            let precise = DateTimeOffset(now.UtcTicks - now.UtcTicks % 10L + 1L, TimeSpan.Zero)

            let future =
                InstallationCheckpoint.create
                    checkpoint.InstallationId
                    checkpoint.Position
                    (Some precise)
                |> Option.get

            let futureFile =
                AdministrationProcess.privateText
                    directory
                    "trusted.json"
                    (Encoding.UTF8.GetString(CheckpointRecord.encode future))

            let! behind = invoke [ "verify"; "--checkpoint"; futureFile ] ExitCode.Diverged

            Expect.equal
                (behind.GetProperty("finding").GetString())
                "EXPIRY_FRONTIER"
                "Trusted cutoff is checked by actual process"

            let! maintenanceRequired =
                invoke
                    [ "admit-restored-installation"; "--checkpoint"; futureFile ]
                    ExitCode.Rejected

            Expect.equal
                (maintenanceRequired.GetProperty("fault").GetString())
                "MAINTENANCE_REQUIRED"
                "Actual restore command requires maintenance"

            let! _ = invoke [ "maintenance"; "on" ] ExitCode.Success

            let! restored =
                invoke
                    [ "admit-restored-installation"; "--checkpoint"; futureFile ]
                    ExitCode.Success

            Expect.equal
                (kind restored)
                "RESTORED_INSTALLATION_ADMITTED"
                "Atomic restore command is published"

            Expect.equal
                (restored.GetProperty("checkpoint").GetProperty("expiredBefore").GetString())
                (CheckpointRecord.formatFrontier precise)
                "Exact cutoff appears in confirmed restore output"

            let! verified = invoke [ "verify"; "--checkpoint"; futureFile ] ExitCode.Success

            Expect.equal
                (kind verified)
                "VERIFIED"
                "Restored frontier now satisfies trusted checkpoint"

            let! rebuilt = invoke [ "rebuild"; "--checkpoint"; futureFile ] ExitCode.Success
            Expect.equal (kind rebuilt) "REBUILT" "Published rebuild accepts trusted evidence"
            let! older = invoke [ "verify"; "--checkpoint"; file ] ExitCode.Success
            Expect.equal (kind older) "VERIFIED" "Older evidence remains valid"

            for arguments, expectedKind, expectedFault in
                [
                    [ "verify"; "--expect"; "0:old" ], "USAGE_ERROR", None
                    [ "rebuild"; "--expect"; "0:old" ], "USAGE_ERROR", None
                    [ "invalidate-restored-sessions" ], "OPERATOR_FAULT", Some "INVOCATION_INVALID"
                ] do
                let expectedExit =
                    if expectedKind = "USAGE_ERROR" then
                        ExitCode.Usage
                    else
                        ExitCode.Rejected

                let! refused = invoke arguments expectedExit
                Expect.equal (kind refused) expectedKind "Exact retired-syntax refusal"

                match expectedFault with
                | Some code ->
                    Expect.equal
                        (refused.GetProperty("fault").GetString())
                        code
                        "Typed invocation refusal"
                | None -> ()

            let invalid =
                AdministrationProcess.privateText
                    directory
                    "invalid.json"
                    "private-checkpoint-content-sentinel"

            let missing = Path.Combine(directory, "private-checkpoint-path-sentinel")
            let noConnection = Path.Combine(directory, "missing-connection-sentinel")

            for path, code in [ invalid, "CHECKPOINT_INVALID"; missing, "CHECKPOINT_FILE_REFUSED" ] do
                let! refused =
                    AdministrationProcess.invoke
                        binary
                        noConnection
                        [ "verify"; "--checkpoint"; path ]
                        (ExitCode.value ExitCode.Rejected)
                    |> Async.AwaitTask

                Expect.equal
                    (kind refused)
                    "OPERATOR_FAULT"
                    "Input failure precedes database/connection admission"

                Expect.equal
                    (refused.GetProperty("fault").GetString())
                    code
                    "Typed private-input diagnostic"

                let output = refused.GetRawText()

                for sentinel in
                    [
                        directory
                        "private-checkpoint-content-sentinel"
                        "private-checkpoint-path-sentinel"
                        noConnection
                    ] do
                    Expect.isFalse
                        (output.Contains sentinel)
                        "Public operator refusal excludes private paths and contents"

            let later =
                InstallationCheckpoint.create
                    checkpoint.InstallationId
                    checkpoint.Position
                    (Some(precise.AddMinutes 1.))
                |> Option.get

            let laterFile =
                AdministrationProcess.privateText
                    directory
                    "later.json"
                    (Encoding.UTF8.GetString(CheckpointRecord.encode later))
            // The head barrier prevents successful completion before the parent closes stdout.
            use blocker = database.OwnerSource.OpenConnection()
            use gate = blocker.BeginTransaction()

            ClaimCore.Postgres.DatabaseSession.rows
                blocker
                "SELECT sequence FROM claimcore.ledger_head FOR UPDATE"
                [||]
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore

            ClaimCore.Postgres.DatabaseSession.execute
                blocker
                "WITH observed AS MATERIALIZED(SELECT clock_timestamp() AS instant) INSERT INTO claimcore.sessions SELECT repeat('b',64),decode('01','hex'),1,instant,instant+interval '1 hour',instant,3600 FROM observed"
                [||]
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore

            let config =
                AdministrationProcess.start
                    [ binary; "admit-restored-installation"; "--checkpoint"; laterFile ]

            config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- connection

            use child =
                Process.Start(config)
                |> Option.ofObj
                |> Option.defaultWith (fun () -> failtest "Restore process did not start")

            try
                child.StandardOutput.Close()
                let errors = child.StandardError.ReadToEndAsync()
                gate.Commit()
                do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
                let! errors = errors |> Async.AwaitTask
                Expect.equal errors "" "Output loss does not expose private provider text"

                Expect.equal
                    child.ExitCode
                    (ExitCode.value ExitCode.OutcomeUnknown)
                    "Committed restore with failed stdout delivery reports unknown outcome"

                use administration =
                    new Administration(
                        database.Container.GetConnectionString(),
                        BuildIdentity.current.Version
                    )

                let! afterLoss = administration.Head CancellationToken.None |> Async.AwaitTask

                Expect.equal
                    afterLoss.ExpiredBefore
                    later.ExpiredBefore
                    "Actual restore commit survived stdout loss"

                let remaining =
                    ClaimCore.Postgres.DatabaseSession.rows
                        blocker
                        "SELECT count(*) FROM claimcore.sessions"
                        [||]
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> List.exactlyOne
                    |> ClaimCore.Postgres.DatabaseSession.value<int64> 0

                Expect.equal
                    remaining
                    0L
                    "Actual committed restore invalidated sessions despite output loss"
            finally
                if not child.HasExited then
                    child.Kill(true)

            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.OtherRead
            )

            let! unsafeFile = invoke [ "verify"; "--checkpoint"; file ] ExitCode.Rejected

            Expect.equal
                (unsafeFile.GetProperty("fault").GetString())
                "CHECKPOINT_FILE_REFUSED"
                "Private-file permission admission applies"

            completed <- true
        finally
            database.Fixture.Dispose()

            if completed then
                Directory.Delete(directory, true)
            else
                printfn "Private checkpoint command evidence retained: %s" directory
    }

[<Tests>]
let tests =
    testCaseAsync "published checkpoint commands and private input refusals" (commandPath ())
