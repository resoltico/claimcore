module internal ClaimCore.IntegrationTests.SessionDrain

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Web.Composition

let private token = CancellationToken.None

let private runtime (database: PostgresFixture.Database) =
    task {
        let settings =
            NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

        settings.Username <- "claimcore_runtime"
        settings.Password <- "synthetic-only"
        settings.MaxPoolSize <- 1
        let application = "claimcore-session-drain-" + Guid.NewGuid().ToString("N")
        settings.ApplicationName <- application

        let! opened =
            ClaimCore.Web.Composition.Runtime.openConnection settings.ConnectionString token

        return
            opened
            |> Result.defaultWith (fun _ -> failtest "Session runtime admission failed"),
            application
    }

let private waitForRow (database: PostgresFixture.Database) application =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let watch = Stopwatch.StartNew()
        let mutable waiting = false

        while not waiting && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock')"
                    [| box application |]
                    token

            waiting <- DatabaseSession.value<bool> 0 rows.Head

            if not waiting then
                do! Task.Delay 10

        Expect.isTrue waiting "The first session holds the runtime's only pooled connection"
    }

let private closedAdmission (runtime: ClaimCore.Web.Composition.IRuntime) key =
    task {
        let mutable rejected = false

        try
            let! _ = (runtime.Sessions.Retrieve(key, token) |> SessionResults.value)
            ()
        with :? InvalidOperationException ->
            rejected <- true

        Expect.isTrue rejected "Shutdown refuses new session users"
    }

let run (database: PostgresFixture.Database) =
    task {
        let storage = SessionStore(database.RuntimeSource) :> ISessionStore

        let create () =
            (storage.Create([| 1uy |], None, TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., token)
             |> SessionResults.value)

        let! first = create ()
        let! second = create ()
        let! admitted, application = runtime database
        use runtime = admitted
        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                holder
                "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                [| box first |]
                token

        let locked = (runtime.Sessions.Retrieve(first, token) |> SessionResults.value)
        do! waitForRow database application
        let queued = (runtime.Sessions.Retrieve(second, token) |> SessionResults.value)
        Expect.isFalse queued.IsCompleted "A second leased session waits for the pool"
        let! bound = runtime.Shutdown(TimeSpan.Zero)

        Expect.equal
            bound
            ShutdownOutcome.BoundExpired
            "Both admitted session users retain their leases"

        do! closedAdmission runtime second
        do! transaction.CommitAsync()
        let! completed = Task.WhenAll(locked, queued)

        Expect.isTrue
            (completed |> Array.forall Option.isSome)
            "The source remains usable until both row and pool waiters drain"

        let! drained = runtime.Shutdown(TimeSpan.FromSeconds 1.)

        Expect.equal
            drained
            ShutdownOutcome.Drained
            "The source closes only after all session users finish"
    }
