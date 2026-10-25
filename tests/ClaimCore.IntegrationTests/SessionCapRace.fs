module internal ClaimCore.IntegrationTests.SessionCapRace

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Postgres

let private token = CancellationToken.None

let private execute (database: PostgresFixture.Database) sql =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection sql [||] token
        return ()
    }

let private prepare database =
    task {
        do! execute database "DELETE FROM claimcore.sessions"

        do!
            execute
                database
                "INSERT INTO claimcore.sessions SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp(),clock_timestamp()+interval '8 hours',clock_timestamp(),1800 FROM generate_series(1,10000) n"

        do!
            execute
                database
                "INSERT INTO claimcore.sessions VALUES (repeat('f',64),decode('01','hex'),1,clock_timestamp()-interval '2 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800)"

        do!
            execute
                database
                "CREATE FUNCTION claimcore.pause_expired_session_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(8851926421::bigint); RETURN OLD; END $$"

        do!
            execute
                database
                "CREATE TRIGGER pause_session_cleanup BEFORE DELETE ON claimcore.sessions FOR EACH ROW EXECUTE FUNCTION claimcore.pause_expired_session_cleanup()"
    }

let private waitForCleanup (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let watch = Stopwatch.StartNew()
        let mutable waiting = false

        while not waiting && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE wait_event_type='Lock' AND wait_event='advisory' AND query LIKE 'DELETE FROM claimcore.sessions%')"
                    [||]
                    token

            waiting <- DatabaseSession.value<bool> 0 rows.Head

            if not waiting then
                do! Task.Delay 10

        Expect.isTrue waiting "Creation passed its clock read and is blocked inside expiry cleanup"
    }

let run (database: PostgresFixture.Database) =
    task {
        do! prepare database
        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                holder
                "SELECT pg_advisory_xact_lock(8851926421::bigint)"
                [||]
                token

        let store = SessionStore(database.RuntimeSource) :> ISessionStore

        let creation =
            store.Create([| 1uy |], None, TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., token)

        do! waitForCleanup database
        // These are ordinary valid UTC touches after the creator's captured instant, not a clock rollback.
        do!
            execute
                database
                "UPDATE claimcore.sessions SET last_activity=clock_timestamp() WHERE absolute_expiry>clock_timestamp()"

        do! transaction.CommitAsync()
        let mutable refused = false

        try
            let! _ = creation
            ()
        with :? InvalidOperationException as failure when
            failure.Message = "The OIDC session store is full." ->
            refused <- true

        Expect.isTrue
            refused
            "Concurrent later activity must not hide active sessions from the creation cap"

        let! rows =
            DatabaseSession.rows
                holder
                "SELECT count(*) FROM claimcore.sessions WHERE absolute_expiry>clock_timestamp()"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            10000L
            "The active-ticket cap remains intact under concurrent touches"
    }
