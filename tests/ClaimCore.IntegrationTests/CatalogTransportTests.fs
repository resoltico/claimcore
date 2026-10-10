module ClaimCore.IntegrationTests.CatalogTransportTests

open System
open System.Data
open System.Diagnostics
open System.IO
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.PostgresReplyDelay

let private cancellableQuery statement _owner app _ =
    use relay = new Relay(app, statement, TimeSpan.FromSeconds 15.)
    use source = RuntimeDataSource.create relay.ConnectionString
    use connection = source.OpenConnection()
    use cancelled = new CancellationTokenSource()
    relay.Arm()
    let elapsed = Stopwatch.StartNew()

    let pending =
        CatalogManifest.requireCompatibleAsyncWithCancellation connection cancelled.Token

    Expect.equal
        (relay.Held.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
        '1'
        "Selected query parse reply is withheld"

    cancelled.Cancel()

    Expect.throws
        (fun () -> pending.WaitAsync(TimeSpan.FromSeconds 9.).GetAwaiter().GetResult())
        "Delayed reply cannot qualify asynchronous admission"

    Expect.isTrue pending.IsCompleted "Admission settled independently of the observation timeout"

    Expect.isLessThan
        elapsed.Elapsed.TotalSeconds
        9.
        "Cancellation and independent cleanup are finite"

    Expect.equal
        connection.State
        ConnectionState.Closed
        "Unsettled cancellation invalidates the connection"

let private alterPrivilege owner sql =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private originalFailureSurvives owner app _ =
    use relay = new Relay(app, "ROLLBACK", TimeSpan.FromSeconds 15.)
    use source = RuntimeDataSource.create relay.ConnectionString
    use connection = source.OpenConnection()
    alterPrivilege owner "GRANT DELETE ON claimcore.cases TO claimcore_app"
    relay.Arm()
    let elapsed = Stopwatch.StartNew()

    try
        let pending =
            CatalogManifest.requireCompatibleAsyncWithCancellation connection CancellationToken.None

        Expect.equal
            (relay.Held.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
            '1'
            "Selected rollback parse reply is withheld"

        try
            pending.WaitAsync(TimeSpan.FromSeconds 12.).GetAwaiter().GetResult()
            failtest "Changed catalog and failed cleanup qualified."
        with :? InvalidDataException as original ->
            Expect.isTrue
                (original.Data.Contains("CatalogAdmissionCleanup"))
                "Cleanup is diagnosed separately on the original divergence"

        Expect.isLessThan
            elapsed.Elapsed.TotalSeconds
            12.
            "Rollback cancellation readback and retirement are bounded"

        Expect.equal
            connection.State
            ConnectionState.Closed
            "Dirty admission cannot return its connector"

        connection.Dispose()
    finally
        alterPrivilege owner "REVOKE DELETE ON claimcore.cases FROM claimcore_app"

let private settings (connection: NpgsqlConnection) =
    use query =
        new NpgsqlCommand(
            "SELECT current_setting('DateStyle'), current_setting('search_path')",
            connection
        )

    use reader = query.ExecuteReader()
    Expect.isTrue (reader.Read()) "Settings are observable"
    reader.GetString(0), reader.GetString(1)

let private releasedReplyLeavesCleanConnection _owner app _ =
    use relay = new Relay(app, "server_version_num", TimeSpan.FromSeconds 15.)
    use source = RuntimeDataSource.create relay.ConnectionString
    use connection = source.OpenConnection()
    let before = settings connection
    relay.Arm()

    let pending =
        CatalogManifest.requireCompatibleAsyncWithCancellation connection CancellationToken.None

    Expect.equal
        (relay.Held.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
        '1'
        "Selected parse reply is withheld"

    Expect.isFalse pending.IsCompleted "Withheld reply prevents admission completion"
    relay.Release()
    pending.WaitAsync(TimeSpan.FromSeconds 9.).GetAwaiter().GetResult()

    Expect.equal
        connection.State
        ConnectionState.Open
        "Successful rollback leaves a reusable connection"

    Expect.equal
        (settings connection)
        before
        "Transaction-local settings do not leak into later work"

    use next = connection.BeginTransaction()
    use probe = new NpgsqlCommand("SELECT 1", connection, next)
    Expect.equal (probe.ExecuteScalar() :?> int) 1 "A fresh transaction owns the clean connection"
    next.Commit()

let tests =
    testList
        "actual catalog transport cancellation and cleanup"
        [
            testCase
                "[CC-DB-001] releasing the selected reply completes admission with clean settings"
                (fun () -> withAuthorityDatabase releasedReplyLeavesCleanConnection)
            testCase
                "[CC-DB-001] settings reply cancellation retires the unclean connection"
                (fun () -> withAuthorityDatabase (cancellableQuery "SET LOCAL DateStyle"))
            testCase
                "[CC-DB-001] version reply cancellation retires the unclean connection"
                (fun () -> withAuthorityDatabase (cancellableQuery "server_version_num"))
            testCase
                "[CC-DB-001] catalog reply cancellation retires the unclean connection"
                (fun () -> withAuthorityDatabase (cancellableQuery "pg_catalog.pg_class"))
            testCase
                "[CC-DB-001] original catalog divergence survives stalled rollback readback"
                (fun () -> withAuthorityDatabase originalFailureSurvives)
        ]
