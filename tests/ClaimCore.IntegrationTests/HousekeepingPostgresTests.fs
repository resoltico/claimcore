module ClaimCore.IntegrationTests.HousekeepingPostgresTests

open System.Threading.Tasks
open Expecto
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private seed database count =
    task {
        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800 FROM generate_series(1,$1) n"
                [| box count |]

        do!
            sql
                database
                "INSERT INTO claimcore.access_request(issuer,principal_kind,subject,first_seen,last_seen) SELECT 'https://issuer.example/','HUMAN','expired-'||n,clock_timestamp()-interval '32 days',clock_timestamp()-interval '31 days' FROM generate_series(1,$1) n"
                [| box count |]
    }

let private lockedRenewal (database: PostgresFixture.Database) =
    task {
        do! seed database 3

        do!
            sql
                database
                "INSERT INTO claimcore.access_request(issuer,principal_kind,subject,first_seen,last_seen) VALUES('https://issuer.example/','HUMAN','fresh',clock_timestamp(),clock_timestamp())"
                [||]

        do! enterMaintenance database
        let! before = InstallationStatus.read database.OwnerSource token
        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                holder
                "SELECT session_key FROM claimcore.sessions WHERE session_key=lpad('1',64,'0') FOR UPDATE"
                [||]
                token

        let! _ =
            DatabaseSession.rows
                holder
                "SELECT subject FROM claimcore.access_request WHERE subject='expired-1' FOR UPDATE"
                [||]
                token

        let! batch = Housekeeping.prune database.RuntimeSource 2 token

        Expect.equal
            (batch.SessionsDeleted, batch.AccessRequestsDeleted)
            (2L, 2L)
            "Bounded batches skip locked rows for both resource families"

        let! _ =
            DatabaseSession.execute
                holder
                "UPDATE claimcore.sessions SET absolute_expiry=clock_timestamp()+interval '1 hour',last_activity=clock_timestamp() WHERE session_key=lpad('1',64,'0'); UPDATE claimcore.access_request SET last_seen=clock_timestamp() WHERE subject='expired-1'"
                [||]
                token

        do! transaction.CommitAsync token
        let! next = Housekeeping.prune database.RuntimeSource 2 token

        Expect.equal
            (next.SessionsDeleted, next.AccessRequestsDeleted)
            (0L, 0L)
            "Renewed and fresh operational rows are not deleted"

        let! after = InstallationStatus.read database.OwnerSource token
        Expect.equal after.Position before.Position "Housekeeping takes no ledger step"

        Expect.isTrue
            after.Maintenance.Active
            "Operational housekeeping is independent of register-write maintenance"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT (SELECT count(*) FROM claimcore.sessions),(SELECT count(*) FROM claimcore.access_request)"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head, DatabaseSession.value<int64> 1 rows.Head)
            (1L, 2L)
            "Independent remaining populations"
    }

let private concurrentHosts (database: PostgresFixture.Database) =
    task {
        do! seed database 8

        let! batches =
            Task.WhenAll(
                Housekeeping.prune database.RuntimeSource 4 token,
                Housekeeping.prune database.RuntimeSource 4 token
            )

        Expect.equal
            (batches |> Array.sumBy _.SessionsDeleted)
            8L
            "Several hosts delete disjoint expired sessions"

        Expect.equal
            (batches |> Array.sumBy _.AccessRequestsDeleted)
            8L
            "Several hosts delete disjoint expired access hints"

        Expect.isTrue
            (batches
             |> Array.forall (fun batch ->
                 batch.SessionsDeleted <= 4L && batch.AccessRequestsDeleted <= 4L))
            "Each individual batch stays bounded"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL host housekeeping"
        [
            testCaseAsync
                "locked expiry candidates survive renewal and fresh rows remain"
                (run lockedRenewal)
            testCaseAsync "concurrent host batches do not double-delete" (run concurrentHosts)
        ]
