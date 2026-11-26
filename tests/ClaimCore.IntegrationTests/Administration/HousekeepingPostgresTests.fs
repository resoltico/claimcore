module ClaimCore.IntegrationTests.HousekeepingPostgresTests

open System
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private seed database count =
    task {
        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800,repeat('b',64) FROM generate_series(1,$1) n"
                [| box count |]

        do!
            sql
                database
                "INSERT INTO claimcore.access_request(principal_key,issuer,principal_kind,subject,first_seen,last_seen) SELECT key,'https://issuer.example/','HUMAN','expired-'||n,clock_timestamp()-interval '32 days',clock_timestamp()-interval '31 days' FROM unnest($1::text[]) WITH ORDINALITY AS seed(key,n)"
                [|
                    box (
                        Array.init count (fun index ->
                            humanPrincipalKey "https://issuer.example/" ($"expired-{index + 1}"))
                    )
                |]
    }

let private lockedRenewal (database: PostgresFixture.Database) =
    task {
        do! seed database 3

        do!
            sql
                database
                "INSERT INTO claimcore.access_request(principal_key,issuer,principal_kind,subject,first_seen,last_seen) VALUES($1,'https://issuer.example/','HUMAN','fresh',clock_timestamp(),clock_timestamp())"
                [| box (humanPrincipalKey "https://issuer.example/" "fresh") |]

        do! enterMaintenance database
        let! before = InstallationStatus.read database.OwnerSource database.MasterKey token
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

        let! batch =
            Housekeeping.prune
                database.RuntimeSource
                database.Installation
                2
                (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                token

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

        let! next =
            Housekeeping.prune
                database.RuntimeSource
                database.Installation
                2
                (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                token

        Expect.equal
            (next.SessionsDeleted, next.AccessRequestsDeleted)
            (0L, 0L)
            "Renewed and fresh operational rows are not deleted"

        let! after = InstallationStatus.read database.OwnerSource database.MasterKey token
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
                Housekeeping.prune
                    database.RuntimeSource
                    database.Installation
                    4
                    (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                    token,
                Housekeeping.prune
                    database.RuntimeSource
                    database.Installation
                    4
                    (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                    token
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

let private rejected expected connection statement parameters =
    task {
        try
            let! _ = DatabaseSession.execute connection statement parameters token
            failtest ("Expected storage rejection " + expected)
        with :? PostgresException as error ->
            Expect.equal error.SqlState expected "The intended storage guard rejected the statement"
    }

let private disclosureRetention (database: PostgresFixture.Database) =
    task {
        use! owner = database.OwnerSource.OpenConnectionAsync()
        use! runtime = database.RuntimeSource.OpenConnectionAsync()
        let actor = Guid.Parse "d1ac105e-0000-4000-8000-000000000001"

        let! times = DatabaseSession.rows owner "SELECT clock_timestamp()" [||] token

        let now = DatabaseSession.instant 0 (List.exactlyOne times)
        let cutoff = now.AddMonths(-25)
        let young = now.AddDays(-2.)
        let prune = "SELECT claimcore.prune_disclosure_trail($1,$2)"

        let! _ =
            DatabaseSession.execute
                owner
                """
                INSERT INTO claimcore.disclosure_trail(recorded_at,actor_id,kind,outcome)
                SELECT $1,$4,'CASE_READ','SUCCEEDED' FROM generate_series(1,130)
                UNION ALL SELECT $2,$4,'CASE_READ','SUCCEEDED'
                UNION ALL SELECT $3,$4,'CASE_READ','SUCCEEDED'
                """
                [| box (cutoff.AddDays(-1.)); box cutoff; box young; box actor |]
                token

        for statement in
            [
                "SELECT id FROM claimcore.disclosure_trail"
                "UPDATE claimcore.disclosure_trail SET kind='TRAIL_READ'"
                "DELETE FROM claimcore.disclosure_trail"
                "TRUNCATE claimcore.disclosure_trail"
                "INSERT INTO claimcore.disclosure_trail(recorded_at,actor_id,kind,outcome) VALUES (clock_timestamp(),'d1ac105e-0000-4000-8000-000000000001','CASE_READ','SUCCEEDED')"
                "UPDATE claimcore.disclosure_retention SET months=1"
            ] do
            do! rejected "42501" runtime statement [||]

        for statement in
            [
                "UPDATE claimcore.disclosure_trail SET kind='TRAIL_READ'"
                "DELETE FROM claimcore.disclosure_trail"
                "TRUNCATE claimcore.disclosure_trail"
                "UPDATE claimcore.disclosure_retention SET months=1"
            ] do
            do! rejected "CC003" owner statement [||]

        let! _ =
            DatabaseSession.execute runtime "SET TimeZone TO 'America/New_York'" [||] token

        do! rejected "CC005" runtime prune [| box young; box 24 |]
        do! rejected "CC005" runtime prune [| box (now.AddMonths(-2)); box 1 |]

        let! first = DatabaseSession.rows runtime prune [| box cutoff; box 24 |] token

        Expect.equal
            (DatabaseSession.value<int64> 0 (List.exactlyOne first))
            128L
            "Direct runtime pruning is bounded even with many eligible rows"

        let! second = DatabaseSession.rows runtime prune [| box cutoff; box 24 |] token

        Expect.equal
            (DatabaseSession.value<int64> 0 (List.exactlyOne second))
            2L
            "Only rows strictly older than the specified cutoff are removed"

        use! ownerTransaction = owner.BeginTransactionAsync()
        let! _ = DatabaseSession.rows owner prune [| box cutoff; box 24 |] token
        do! rejected "CC003" owner "DELETE FROM claimcore.disclosure_trail" [||]
        do! ownerTransaction.RollbackAsync()

        let! report =
            Housekeeping.prune
                database.RuntimeSource
                database.Installation
                2
                (fun instant -> DateOnly.FromDateTime instant.UtcDateTime)
                token

        Expect.isGreaterThanOrEqual
            report.ObservedAt
            now
            "The host obtains the pruning instant from the database"

        let! surviving =
            DatabaseSession.rows
                owner
                "SELECT recorded_at FROM claimcore.disclosure_trail"
                [||]
                token

        Expect.equal
            (surviving |> List.map (DatabaseSession.instant 0))
            [ young ]
            "Housekeeping uses the 24-month Core cutoff and leaves recent evidence"
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
            testCaseAsync
                "disclosure pruning requires the admitted retention and preserves recent evidence"
                (run disclosureRetention)
        ]
