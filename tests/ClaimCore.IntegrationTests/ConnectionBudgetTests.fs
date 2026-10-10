module ClaimCore.IntegrationTests.ConnectionBudgetTests

open System
open System.Data
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private raw user =
    let builder = NpgsqlConnectionStringBuilder()
    builder.Host <- "localhost"
    builder.Username <- user
    builder.Timeout <- 0
    builder.CommandTimeout <- 0
    builder.CancellationTimeout <- 0
    builder.ConnectionString

let private budgets command (builder: NpgsqlConnectionStringBuilder) =
    Expect.equal builder.Timeout 5 "Connection establishment is finite"
    Expect.equal builder.CommandTimeout command "Ordinary commands are finite"
    Expect.equal builder.CancellationTimeout 2000 "Cancellation readback is finite"

let private compiledBuilders () =
    use primary = RuntimeDataSource.create (raw "claimcore_app")
    budgets 10 (NpgsqlConnectionStringBuilder(primary.ConnectionString))
    budgets 30 (OwnerConnection.builder (raw "synthetic_owner"))

    budgets
        30
        (NpgsqlConnectionStringBuilder(
            PostgresTransport.connectionString (raw "claimcore_witness_writer")
        ))

let private settings (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            "SELECT current_setting('DateStyle'), current_setting('search_path')",
            connection
        )

    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Settings are present"
    reader.GetString(0), reader.GetString(1)

let private transactionLocalAdmission owner _ _ =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use alter =
        new NpgsqlCommand("SET DateStyle = 'SQL, DMY'; SET search_path = ''", connection)

    alter.ExecuteNonQuery() |> ignore
    let before = settings connection

    CatalogManifest.requireCompatibleAsyncWithCancellation connection CancellationToken.None
    |> await

    Expect.equal (settings connection) before "Admission rolls back transaction-local settings"
    use transaction = connection.BeginTransaction()
    Expect.equal (settings connection) before "Later work inherits unchanged settings"
    transaction.Rollback()
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()

    Expect.throwsT<OperationCanceledException>
        (fun () ->
            CatalogManifest.requireCompatibleAsyncWithCancellation connection cancelled.Token
            |> await)
        "Pre-work cancellation remains the original refusal"

    Expect.equal (settings connection) before "Cancelled admission leaves a clean connection"

let private acquisitionBudget explicitSeconds owner app _ =
    use blocker = new NpgsqlConnection(owner)
    blocker.Open()

    use held =
        AuthorityOperationFence.acquireShared None blocker CancellationToken.None
        |> await

    use source = RuntimeDataSource.create app
    use queued = source.OpenConnection()
    let elapsed = Diagnostics.Stopwatch.StartNew()

    let pending =
        match explicitSeconds with
        | Some seconds ->
            AuthorityOperationFence.acquireExclusiveWithin
                (Some source)
                queued
                seconds
                CancellationToken.None
        | None ->
            AuthorityOperationFence.acquireExclusive (Some source) queued CancellationToken.None

    Expect.throwsT<NpgsqlException>
        (fun () -> pending.WaitAsync(TimeSpan.FromSeconds 20.).GetAwaiter().GetResult() |> ignore)
        "The admitted command budget expires without a caller cancellation token"

    let minimum = explicitSeconds |> Option.defaultValue 10 |> float

    Expect.isGreaterThanOrEqual
        elapsed.Elapsed.TotalSeconds
        (minimum - 0.5)
        "The requested acquisition window remains available"

    Expect.isLessThan
        elapsed.Elapsed.TotalSeconds
        16.
        "Acquisition and cancellation settlement remain finite"

    Expect.equal queued.State ConnectionState.Closed "An uncertain session-level grant is retired"

let tests =
    testList
        "admitted PostgreSQL wait and settings policy"
        [
            testCase
                "[CC-AUDIT-001] default authority acquisition expires without caller cancellation"
                (fun () -> withAuthorityDatabase (acquisitionBudget None))
            testCase
                "[CC-AUDIT-001] explicit longer authority acquisition expires without caller cancellation"
                (fun () -> withAuthorityDatabase (acquisitionBudget (Some 15)))
            testCase
                "[CC-DB-001] actual builders normalize infinite transport and cancellation budgets"
                compiledBuilders
            testCase
                "[CC-DB-001] asynchronous catalog admission preserves settings through later work and cancellation"
                (fun () -> withAuthorityDatabase transactionLocalAdmission)
        ]
