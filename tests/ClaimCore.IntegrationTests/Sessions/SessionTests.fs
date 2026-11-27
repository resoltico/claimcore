module ClaimCore.IntegrationTests.SessionTests

open ClaimCore.TestSupport

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification

let private token = CancellationToken.None
let private payload = [| 1uy; 2uy; 3uy |]

let private create (store: ISessionStore) =
    (store.Create(
        SessionResults.principal,
        None,
        (fun _ -> payload),
        None,
        TimeSpan.FromMinutes 30.,
        TimeSpan.FromHours 8.,
        token
     )
     |> SessionResults.value)

let private sql (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement parameters token
        return ()
    }

let private lifetime (database: PostgresFixture.Database) key =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT absolute_expiry,idle_seconds FROM claimcore.sessions WHERE session_key=$1"
                [| box key |]
                token

        return DatabaseSession.instant 0 rows.Head, DatabaseSession.value<double> 1 rows.Head
    }

let private lifecycle (database: PostgresFixture.Database) =
    task {
        let first =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let second =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create first
        let! read = (second.Retrieve(key, token) |> SessionResults.payload)
        Expect.equal read (Some payload) "Another instance reads the committed session"

        let restarted =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! restored = (restarted.Retrieve(key, token) |> SessionResults.payload)
        Expect.equal restored read "A newly constructed instance needs no startup load"
        let! beforeRenewal = lifetime database key

        do!
            (second.Renew(key, SessionResults.principal, [| 4uy; 5uy |], (fun _ -> true), token)
             |> SessionResults.value)

        let! afterRenewal = lifetime database key

        Expect.equal
            afterRenewal
            beforeRenewal
            "Renewal preserves the original absolute and idle settings"

        let! renewed = (first.Retrieve(key, token) |> SessionResults.payload)
        Expect.equal renewed (Some [| 4uy; 5uy |]) "Renewal replaces only the protected payload"
        do! (first.Remove(key, SessionEndReason.Logout, token) |> SessionResults.value)

        do!
            (second.Renew(key, SessionResults.principal, payload, (fun _ -> true), token)
             |> SessionResults.value)

        let! removed = (restarted.Retrieve(key, token) |> SessionResults.payload)
        Expect.isNone removed "Renew never recreates a logged-out session"
    }

let private deadlines (database: PostgresFixture.Database) =
    task {
        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create store

        do!
            sql
                database
                "UPDATE claimcore.sessions SET last_activity=clock_timestamp()+interval '1 hour' WHERE session_key=$1"
                [| box key |]

        let! backward = (store.Retrieve(key, token) |> SessionResults.payload)
        Expect.isNone backward "A backward observation invalidates the session"
        let! idle = create store

        do!
            sql
                database
                "UPDATE claimcore.sessions SET created_at=clock_timestamp()-interval '1 hour',last_activity=clock_timestamp()-interval '30 minutes' WHERE session_key=$1"
                [| box idle |]

        let! expired = (store.Retrieve(idle, token) |> SessionResults.payload)
        Expect.isNone expired "The idle deadline refuses at equality and after"
        let! absolute = create store

        do!
            sql
                database
                "UPDATE claimcore.sessions SET created_at=clock_timestamp()-interval '2 hours',absolute_expiry=clock_timestamp()-interval '1 hour' WHERE session_key=$1"
                [| box absolute |]

        do!
            (store.Renew(absolute, SessionResults.principal, payload, (fun _ -> true), token)
             |> SessionResults.value)

        let! ended = (store.Retrieve(absolute, token) |> SessionResults.payload)
        Expect.isNone ended "Renew refuses expired absolute deadlines"
    }

let private deadlineAfterLock (database: PostgresFixture.Database) =
    task {
        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create store

        do!
            sql
                database
                "UPDATE claimcore.sessions SET absolute_expiry=clock_timestamp()+interval '300 milliseconds' WHERE session_key=$1"
                [| box key |]

        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                connection
                "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                [| box key |]
                token

        let retrieval = (store.Retrieve(key, token) |> SessionResults.payload)
        do! Task.Delay 400
        do! transaction.CommitAsync()
        let! result = retrieval
        Expect.isNone result "Deadline uses a fresh clock after waiting for the row lock"
    }

let private exactDeadline () =
    let instant = DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)

    let row (expiry: DateTime) (activity: DateTime) : obj array =
        [|
            payload :> obj
            box 1 |> nonNull
            box expiry |> nonNull
            box activity |> nonNull
            box 1800. |> nonNull
        |]

    Expect.isFalse
        (SessionRows.valid instant (row instant.UtcDateTime (instant.AddMinutes(-1.).UtcDateTime)))
        "Absolute deadline equality refuses"

    Expect.isFalse
        (SessionRows.valid
            instant
            (row (instant.AddHours(1.).UtcDateTime) (instant.AddMinutes(-30.).UtcDateTime)))
        "Idle deadline equality refuses"

    Expect.isFalse
        (SessionRows.valid
            instant
            (row (instant.AddHours(1.).UtcDateTime) (instant.AddTicks(1L).UtcDateTime)))
        "Observed backward time refuses"

let private logoutRace (database: PostgresFixture.Database) =
    task {
        let first =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let second =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create first
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "DELETE FROM claimcore.sessions WHERE session_key=$1"
                [| box key |]
                token

        let renewal =
            (second.Renew(key, SessionResults.principal, payload, (fun _ -> true), token)
             |> SessionResults.value)

        let retrieval = (first.Retrieve(key, token) |> SessionResults.payload)
        do! Task.Delay 100
        Expect.isFalse renewal.IsCompleted "Renew waits for the row lock"
        Expect.isFalse retrieval.IsCompleted "Retrieve waits for the row lock"
        do! transaction.CommitAsync()
        do! renewal
        let! result = retrieval
        Expect.isNone result "The committed delete wins against waiting readers"
    }

let private independentRows (database: PostgresFixture.Database) =
    task {
        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! blockedKey = create store
        let! otherKey = create store
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ = Transactions.head connection token

        let! _ =
            DatabaseSession.rows
                connection
                "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                [| box blockedKey |]
                token

        let creation = create store
        let blocked = (store.Retrieve(blockedKey, token) |> SessionResults.payload)
        let other = (store.Retrieve(otherKey, token) |> SessionResults.payload)
        let! completed = Task.WhenAny(other :> Task, Task.Delay 3000)

        Expect.isTrue
            (obj.ReferenceEquals(completed, other))
            "Different session rows progress independently"

        let! _ = creation.WaitAsync(TimeSpan.FromSeconds 3.)
        Expect.isFalse blocked.IsCompleted "The selected ticket remains locked"

        let! another = (store.Retrieve(otherKey, token) |> SessionResults.payload)
        Expect.isSome another "Session lookup does not acquire the ledger head"
        do! transaction.RollbackAsync()
        let! _ = blocked
        return ()
    }

let private boundedWait (database: PostgresFixture.Database) =
    task {
        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create store
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                connection
                "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                [| box key |]
                token

        let watch = System.Diagnostics.Stopwatch.StartNew()
        let mutable timedOut = false

        try
            let! _ = (store.Retrieve(key, token) |> SessionResults.payload)
            ()
        with :? Npgsql.PostgresException as failure when failure.SqlState = "55P03" ->
            timedOut <- true

        Expect.isTrue timedOut "The configured row-lock timeout refuses a blocked session lookup"

        Expect.isLessThan
            watch.Elapsed
            (TimeSpan.FromSeconds 8.)
            "A row lock cannot retain authentication admission indefinitely"

        use cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.)
        let mutable aborted = false

        try
            let! _ = (store.Retrieve(key, cancelled.Token) |> SessionResults.payload)
            ()
        with :? OperationCanceledException ->
            aborted <- true

        Expect.isTrue aborted "Caller cancellation interrupts actual database waiting"
        do! transaction.RollbackAsync()
        let! recovered = (store.Retrieve(key, token) |> SessionResults.payload)

        Expect.isSome
            recovered
            "Timeout and cancellation release their transactions without deleting a live session"
    }

let private capacity (database: PostgresFixture.Database) =
    task {
        do! sql database "DELETE FROM claimcore.sessions" [||]

        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key,recovery_generation) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),2,clock_timestamp(),clock_timestamp()+interval '8 hours',clock_timestamp(),1800,repeat('b',64),(SELECT generation FROM claimcore.recovery_generation) FROM generate_series(1,10000) n"
                [||]

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let mutable expired = false

        try
            let! _ =
                store.Create(
                    SessionResults.principal,
                    None,
                    (fun _ -> payload),
                    Some(DateTimeOffset.UtcNow.AddMinutes(-1.0)),
                    TimeSpan.FromMinutes 30.,
                    TimeSpan.FromHours 8.,
                    token
                )

            ()
        with :? System.Security.Authentication.AuthenticationException ->
            expired <- true

        Expect.isTrue
            expired
            "An expired authentication ticket is refused before the capacity check"

        let mutable refused = false

        try
            let! _ = create store
            ()
        with :? System.IO.IOException ->
            refused <- true

        Expect.isTrue refused "Creation cap rejects the 10001st active session"

        do!
            sql
                database
                "UPDATE claimcore.sessions SET created_at=clock_timestamp()-interval '2 hours',absolute_expiry=clock_timestamp()-interval '1 hour'"
                [||]

        let! key = create store
        let! found = (store.Retrieve(key, token) |> SessionResults.payload)
        Expect.isSome found "Bounded cleanup permits new active sessions after expiry"
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows connection "SELECT count(*) FROM claimcore.sessions" [||] token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            9873L
            "Creation removes at most 128 expired rows"
    }

let private storeFailure (database: PostgresFixture.Database) =
    task {
        use source =
            Npgsql.NpgsqlDataSource.Create(PostgresFixture.ownerConnectionString database)

        let store = SessionStore(source, database.Installation) :> ISessionStore
        let! key = create store
        source.Dispose()
        let mutable failed = false

        try
            let! _ = (store.Retrieve(key, token) |> SessionResults.payload)
            ()
        with :? ObjectDisposedException ->
            failed <- true

        Expect.isTrue failed "A failed storage boundary is observable, distinct from a missing row"
    }

let private suite () =
    task {
        let! database = PostgresFixture.create ()

        try
            let! _ = StoreFixtures.bootstrap database.Fixture
            do! SessionHosts.run database
            do! lifecycle database
            do! deadlines database
            do! deadlineAfterLock database
            exactDeadline ()
            do! logoutRace database
            do! independentRows database
            do! boundedWait database
            do! capacity database
            do! storeFailure database
            do! SessionDrain.run database
            do! SessionCapRace.run database
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL sessions restart, deadlines and cross-instance concurrency"
        (async { do! suite () |> Async.AwaitTask })
