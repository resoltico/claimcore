module ClaimCore.IntegrationTests.AdministrationSignalTests

open System
open System.Threading
open Microsoft.Extensions.Logging
open Npgsql
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private signalled () =
    task {
        let! database = PostgresFixture.create ()
        let directory = PrivateTestPaths.newPrivateDirectory "claimcore-owner-signals-"
        let mutable complete = false

        try
            let configuration =
                typeof<Administration>
                    .Assembly
                    .GetCustomAttributes(typeof<Reflection.AssemblyConfigurationAttribute>, false)
                |> Array.exactlyOne
                |> unbox<Reflection.AssemblyConfigurationAttribute>
                |> _.Configuration

            let! binary =
                AdministrationProcess.publishConfiguration
                    configuration
                    (Path.Combine(directory, "published"))

            let connection =
                AdministrationProcess.privateText
                    directory
                    "connection"
                    (database.Container.GetConnectionString())

            let! _ = AdministrationProcess.invoke binary connection [ "status" ] 0
            let! checkpoint = LedgerAdministration.head database.OwnerSource token

            let checkpointFile =
                AdministrationProcess.privateText
                    directory
                    "checkpoint"
                    (System.Text.Encoding.UTF8.GetString(
                        ClaimCore.RecordFormat.CheckpointRecord.encode checkpoint
                    ))

            let config =
                AdministrationProcess.start [ binary; "rebuild"; "--checkpoint"; checkpointFile ]

            config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- connection
            let! _, refusal, quiet = AdministrationProcess.execute config
            use refusalDocument = JsonDocument.Parse refusal

            Expect.equal
                (refusalDocument.RootElement.GetProperty("fault").GetString())
                "MAINTENANCE_REQUIRED"
                "Expected owner refusal"

            Expect.equal quiet "" "Business refusal is not a storage diagnostic"

            for signal in [ "INT"; "TERM" ] do
                use! holder = database.OwnerSource.OpenConnectionAsync()
                use! held = holder.BeginTransactionAsync()
                let! _ = Transactions.head holder token
                let config = AdministrationProcess.start [ binary; "maintenance"; "on" ]
                config.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- connection

                use child =
                    Process.Start(config)
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failtest "Owner process did not start")

                let output = child.StandardOutput.ReadToEndAsync()
                let errors = child.StandardError.ReadToEndAsync()

                try
                    use! monitor = database.OwnerSource.OpenConnectionAsync()
                    let timer = Stopwatch.StartNew()
                    let mutable waiting = false

                    while not waiting && timer.Elapsed < TimeSpan.FromSeconds 10. do
                        let! rows =
                            DatabaseSession.rows
                                monitor
                                "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%claimcore.lock_ledger_head()%')"
                                [||]
                                token

                        waiting <- DatabaseSession.value<bool> 0 rows.Head

                        if not waiting then
                            do! Task.Delay 20

                    Expect.isTrue waiting "The published command reached real work before COMMIT"
                    let kill = ProcessStartInfo("/bin/kill", UseShellExecute = false)
                    kill.ArgumentList.Add("-" + signal)
                    kill.ArgumentList.Add(string child.Id)

                    use sender =
                        Process.Start(kill)
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failtest "Signal sender did not start")

                    do! sender.WaitForExitAsync()
                    Expect.equal sender.ExitCode 0 "Applicable POSIX signal delivered"
                    do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 15.)
                    let! output = output
                    let! errors = errors

                    Expect.equal
                        child.ExitCode
                        3
                        "Definite cancellation uses the public failed exit"

                    Expect.equal
                        (output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
                        1
                        "One reply after awaited cleanup"

                    use reply = JsonDocument.Parse output

                    Expect.equal
                        (reply.RootElement.GetProperty("fault").GetString())
                        "CANCELLED"
                        "Pre-dispatch cancellation is not outcome unknown"

                    AdministrationProcess.requireDiagnostics errors

                    Expect.isGreaterThan
                        errors.Length
                        0
                        "The production sink reports the cancelled storage attempt"

                    do! held.RollbackAsync()
                    let! status = InstallationStatus.read database.OwnerSource token

                    Expect.isFalse
                        status.Maintenance.Active
                        "No mutation committed before cancellation"

                    let! positive =
                        RegisterMaintenanceStore.change
                            database.OwnerSource
                            MaintenanceChange.On
                            token

                    Expect.isTrue
                        positive.State.Active
                        "Locks and connections are independently reusable"

                    let! _ =
                        RegisterMaintenanceStore.change
                            database.OwnerSource
                            MaintenanceChange.Off
                            token

                    ()
                finally
                    if not child.HasExited then
                        child.Kill(true)

            complete <- true
        finally
            database.Fixture.Dispose()

            if complete then
                Directory.Delete(directory, true)
    }

type private CommitCancellationLogger(onCommit: unit -> unit) =
    interface ILogger with
        member _.IsEnabled _ = true

        member _.BeginScope<'TState when 'TState: not null>(_: 'TState) =
            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.Log<'TState>
            (
                _: LogLevel,
                event: EventId,
                _: 'TState,
                _: exn | null,
                _: Func<'TState, exn | null, string>
            ) =
            if event.Name = "CommittedTransaction" then
                onCommit ()

let private confirmedCleanup () =
    task {
        let! database = PostgresFixture.create ()

        try
            use cancellation = new CancellationTokenSource()

            let mutable confirmedBeforeReturn = false

            use factory =
                { new ILoggerFactory with
                    member _.AddProvider _ = ()

                    member _.CreateLogger _ =
                        CommitCancellationLogger(fun () ->
                            confirmedBeforeReturn <- true
                            cancellation.Cancel())
                        :> ILogger

                    member _.Dispose() = ()
                }

            let builder = NpgsqlDataSourceBuilder(database.Container.GetConnectionString())
            builder.UseLoggerFactory factory |> ignore
            use source = builder.Build()
            // Npgsql 10.0.3 emits CommittedTransaction after awaiting the server's COMMIT response:
            // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlTransaction.cs#L125-L126
            let! confirmed =
                RegisterMaintenanceStore.change source MaintenanceChange.On cancellation.Token

            Expect.isTrue
                confirmedBeforeReturn
                "The actual admin command is cancelled after provider confirmation, before returning"

            Expect.isTrue
                cancellation.IsCancellationRequested
                "The command token really was cancelled"

            Expect.isTrue
                confirmed.State.Active
                "Confirmed administrative success is not converted into cancellation or uncertainty"

            use cancellation = new CancellationTokenSource()

            use administration =
                new Administration(
                    database.Container.GetConnectionString(),
                    ClaimCore.Wire.BuildIdentity.current.Version
                )

            let! report = administration.Maintenance(MaintenanceChange.On, cancellation.Token)
            // Cancellation is deliberately after the actual public admin API confirmed COMMIT,
            // and before owner disposal. It cannot retrospectively replace the accepted report.
            cancellation.Cancel()
            (administration :> IDisposable).Dispose()

            Expect.isTrue
                cancellation.IsCancellationRequested
                "Real command token cancelled after confirmed admin success"

            Expect.isTrue
                report.State.Active
                "Confirmed administrative report survives late cancellation and disposal"

            use independent =
                new Administration(
                    database.Container.GetConnectionString(),
                    ClaimCore.Wire.BuildIdentity.current.Version
                )

            let! status = independent.Status token

            Expect.isTrue
                status.Maintenance.Active
                "Independent owner connection confirms the actual committed mutation"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "published administration signals"
        [
            testCaseAsync
                "confirmed administrative commit survives late cancellation and owner disposal"
                (async { do! confirmedCleanup () |> Async.AwaitTask })
            testCaseAsync
                "SIGINT and SIGTERM cancel blocked owner work and await cleanup"
                (async { do! signalled () |> Async.AwaitTask })
        ]
