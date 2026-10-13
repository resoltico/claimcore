module ClaimCore.IntegrationTests.TransactionTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private sql (database: PostgresFixture.Database) text =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection text [||] token
        return ()
    }

let private waitForWaiter (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let mutable found = false
        let deadline = DateTimeOffset.UtcNow.AddSeconds 3.

        while not found && DateTimeOffset.UtcNow < deadline do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock')"
                    [||]
                    token

            found <- DatabaseSession.value<bool> 0 rows.Head

            if not found then
                do! Task.Delay 10

        Expect.isTrue found "Writer is waiting on the head lock"
    }

let private instantOrdering (database: PostgresFixture.Database) =
    task {
        use! holder = database.RuntimeSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()
        let! _ = Transactions.head holder token

        let waiter =
            Transactions.write database.RuntimeSource Transactions.timeouts token (fun connection ->
                task {
                    let! _, instant, _ = Transactions.head connection token
                    return instant, false
                })

        do! waitForWaiter database
        let! rows = DatabaseSession.rows holder "SELECT clock_timestamp()" [||] token
        let precedingInstant = DatabaseSession.instant 0 rows.Head
        do! transaction.CommitAsync()
        let! result = waiter

        return
            match result with
            | Ok instant -> instant > precedingInstant
            | Error _ -> false
    }

let private waiter (database: PostgresFixture.Database) settings =
    let store =
        LedgerStore(database.RuntimeSource, database.Fixture.InstallationId, settings)
        :> ILedgerStore

    let result =
        store.WriteCase(
            database.Fixture.Owner,
            id 20,
            "synthetic-timeout",
            (fun _ -> CaseDecision.Refuse Rejection.ResourceUnavailable),
            token
        )

    result

let private scenarios () =
    let statements =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "SchemaScenarios.sql"))
        |> Array.filter (fun line ->
            not (
                line.StartsWith("\\", StringComparison.Ordinal)
                || line.StartsWith("--", StringComparison.Ordinal)
            ))
        |> String.concat "\n"

    statements

let private refusalCodes =
    let codes =
        [ "CC001"; "CC001"; "23505" ]
        @ List.replicate 6 "42501"
        @ [
            "CC002"
            "23514"
            "23514"
            "23514"
            "23514"
            "23505"
            "23514"
            "23514"
            "23505"
        ]

    codes

let private snapshot (result: Result<CaseReadView, StoreFailure>) name hasEditor =
    match result with
    | Ok {
             Actor = Some actor
             Case = Some {
                             State = StoredCaseState.Live(view, _)
                         }
         } ->
        Expect.equal
            (actor.Grants |> List.exists (fun grant -> grant.Role = Role.CaseEditor))
            hasEditor
            "Authority belongs to this snapshot"

        Expect.equal view.Fields.ClaimantName name "Data belongs to this snapshot"
    | _ -> failtest "Expected a coherent read"

[<Tests>]
let tests =
    testSequenced
    <| testList
        "PostgreSQL transactions"
        [
            testCaseAsync
                "concurrent writers decide once on current heads and produce a gapless chain"
                (run (fun database ->
                    task {
                        let! actor = editor database.Fixture

                        let tasks =
                            [|
                                for index in 1..20 ->
                                    actor.Execute(
                                        request
                                            (100 + index)
                                            ($"synthetic-{index}")
                                            0L
                                            (ClaimCore.Domain.Command.Open registration),
                                        token
                                    )
                            |]

                        let! results = Task.WhenAll tasks

                        let positions =
                            results |> Array.map (accepted >> _.Position.Sequence) |> Array.sort

                        Expect.equal
                            positions
                            [| 3L .. 22L |]
                            "Each locked decision saw the current sequence"

                        let! verified = Verification.verify database.OwnerSource None token
                        Expect.equal verified.Sequence 22L "All links recompute"
                    }))
            testCaseAsync
                "database instant is read after the lock wait; pre-lock clock is a failing control"
                (run (fun database ->
                    task {
                        let! ordered = instantOrdering database
                        Expect.isTrue ordered "Instant follows prior holder"

                        do!
                            sql
                                database
                                "CREATE OR REPLACE FUNCTION claimcore.lock_ledger_head(OUT sequence bigint,OUT entry_hash bytea,OUT observed_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN SELECT h.sequence,h.entry_hash,clock_timestamp() INTO STRICT sequence,entry_hash,observed_at FROM claimcore.ledger_head h WHERE singleton FOR UPDATE; END $$"

                        let! broken = instantOrdering database
                        Expect.isFalse broken "Pre-lock clock must be detected"
                    }))
            testCaseAsync
                "lock timeout is definite and a disabled timeout fails the negative control"
                (run (fun database ->
                    task {
                        let! actor = editor database.Fixture
                        use! holder = database.RuntimeSource.OpenConnectionAsync()
                        use! transaction = holder.BeginTransactionAsync()
                        let! _ = Transactions.head holder token


                        let! timed =
                            waiter
                                database
                                { Transactions.timeouts with
                                    LockMilliseconds = 100
                                }

                        Expect.equal
                            timed
                            (Error StoreFailure.Unavailable)
                            "Before commit is definite"

                        let noTimeout =
                            waiter
                                database
                                { Transactions.timeouts with
                                    LockMilliseconds = 0
                                }

                        let! _ = Task.WhenAny(noTimeout :> Task, Task.Delay 200)

                        Expect.isFalse
                            noTimeout.IsCompleted
                            "Disabling guard leaves waiter blocked"

                        do! transaction.RollbackAsync()
                        let! recovered = noTimeout

                        Expect.equal
                            recovered
                            (Ok(CaseWriteResult.Refused Rejection.ResourceUnavailable))
                            "Next writer proceeds"

                        let! head = Verification.verify database.OwnerSource None token
                        Expect.equal head.Sequence 2L "Neither waiter wrote"
                        let! _ = actor.Get("absent", token)
                        return ()
                    }))
            testCaseAsync
                "statement timeout cancels a real statement; disabled guard permits it"
                (run (fun database ->
                    task {
                        for milliseconds, expected in [ 100, true; 0, false ] do
                            use! connection = database.RuntimeSource.OpenConnectionAsync()
                            use! _transaction = connection.BeginTransactionAsync()

                            let! _ =
                                Transactions.configure
                                    connection
                                    { Transactions.timeouts with
                                        StatementMilliseconds = milliseconds
                                    }
                                    token

                            let mutable cancelled = false

                            try
                                let! _ =
                                    DatabaseSession.rows
                                        connection
                                        "SELECT pg_sleep(0.2)"
                                        [||]
                                        token in

                                ()
                            with :? PostgresException as error ->
                                cancelled <- error.SqlState = "57014"

                            Expect.equal cancelled expected "Statement guard detects slow work"
                    }))
            testCaseAsync
                "idle transaction timeout ends its holder; disabled guard leaves it alive"
                (run (fun database ->
                    task {
                        for milliseconds, expected in [ 100, true; 0, false ] do
                            use! connection = database.RuntimeSource.OpenConnectionAsync()
                            use! _transaction = connection.BeginTransactionAsync()

                            let! _ =
                                Transactions.configure
                                    connection
                                    { Transactions.timeouts with
                                        IdleMilliseconds = milliseconds
                                    }
                                    token

                            let! _ = Transactions.head connection token
                            do! Task.Delay 250
                            let mutable ended = false

                            try
                                let! _ = DatabaseSession.rows connection "SELECT 1" [||] token in
                                ()
                            with :? NpgsqlException ->
                                ended <- true

                            Expect.equal ended expected "Idle guard controls holder lifetime"

                            if expected then
                                let! head =
                                    Transactions.write
                                        database.RuntimeSource
                                        Transactions.timeouts
                                        token
                                        (fun connection ->
                                            task {
                                                let! head, _, _ =
                                                    Transactions.head connection token in

                                                return head.Sequence, false
                                            })

                                Expect.equal head (Ok 0L) "Next writer obtains released lock"
                    }))
            testCaseAsync
                "deferred commit error is definite and rolls back entry and projection"
                (run (fun database ->
                    task {
                        let! actor = editor database.Fixture

                        do!
                            sql
                                database
                                "CREATE FUNCTION claimcore.reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic deferred refusal' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_commit AFTER INSERT ON claimcore.cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.reject_commit()"

                        let operation =
                            request
                                20
                                "synthetic-deferred"
                                0L
                                (ClaimCore.Domain.Command.Open registration)

                        let! failed = actor.Execute(operation, token)

                        Expect.equal
                            failed
                            (ExecuteOutcome.Failed CoreFault.StoreUnavailable)
                            "SQLSTATE is definite"

                        let! head = Verification.verify database.OwnerSource None token
                        Expect.equal head.Sequence 2L "Failed commit did not append"
                        do! sql database "DROP TRIGGER reject_commit ON claimcore.cases"
                        let! retry = actor.Execute(operation, token)
                        accepted retry |> ignore
                    }))
            testCaseAsync
                "runtime exercises all eighteen structural and privilege refusals"
                (run (fun database ->
                    task {
                        let scenarios = scenarios ()
                        use! connection = database.RuntimeSource.OpenConnectionAsync()
                        let codes = ResizeArray<string>()

                        for statement in
                            scenarios.Split(';', StringSplitOptions.RemoveEmptyEntries) do
                            try
                                let! _ = DatabaseSession.execute connection statement [||] token in
                                ()
                            with :? PostgresException as error ->
                                codes.Add error.SqlState


                        Expect.equal
                            (List.ofSeq codes)
                            refusalCodes
                            "Every refusal has its exact SQLSTATE"

                        let! rows =
                            DatabaseSession.rows
                                connection
                                "SELECT sequence,payload IS NULL,payload_nonce IS NULL,request_sha256 IS NULL FROM claimcore.ledger ORDER BY sequence"
                                [||]
                                token

                        Expect.equal rows.Length 2 "Accepted erasure extends head"

                        for row in rows do
                            for column in 1..3 do
                                Expect.isTrue
                                    (DatabaseSession.value<bool> column row)
                                    "Every payload column cleared"

                        let! heads =
                            DatabaseSession.rows
                                connection
                                "SELECT sequence FROM claimcore.ledger_head"
                                [||]
                                token

                        Expect.equal
                            (DatabaseSession.value<int64> 0 heads.Head)
                            2L
                            "Rejected inserts never moved head"
                    }))
            testCaseAsync
                "stale-head guard rejects a fork; disabling the trigger is detected"
                (run (fun database ->
                    task {
                        let insert =
                            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,kind,operation_id,actor_id,recorded_at,payload_commitment) VALUES (2,decode(repeat('00',32),'hex'),decode(repeat('11',32),'hex'),1,'AUTHORITY','80000000-0000-4000-8000-000000000001','80000000-0000-4000-8000-000000000001',clock_timestamp(),decode(repeat('00',32),'hex'))"

                        use! connection = database.RuntimeSource.OpenConnectionAsync()

                        let attempt () =
                            task {
                                try
                                    let! _ = DatabaseSession.execute connection insert [||] token in
                                    return None
                                with :? PostgresException as error ->
                                    return Some error.SqlState
                            }

                        let! guarded = attempt ()
                        Expect.equal guarded (Some "CC001") "Fork is an integrity refusal"

                        do!
                            sql
                                database
                                "ALTER TABLE claimcore.ledger DISABLE TRIGGER ledger_extend_head"

                        let! unguarded = attempt ()
                        Expect.isNone unguarded "Without the guard, the same fork wrongly succeeds"
                    }))
            testCaseAsync
                "case data and authority remain in one snapshot across a concurrent change"
                (run (fun database ->
                    task {
                        let! actor = editor database.Fixture

                        let! opened =
                            actor.Execute(
                                request
                                    20
                                    "synthetic-snapshot"
                                    0L
                                    (ClaimCore.Domain.Command.Open registration),
                                token
                            )

                        accepted opened |> ignore
                        use! holder = database.OwnerSource.OpenConnectionAsync()
                        use! transaction = holder.BeginTransactionAsync()

                        let! _ =
                            DatabaseSession.execute
                                holder
                                "LOCK TABLE claimcore.cases IN ACCESS EXCLUSIVE MODE"
                                [||]
                                token

                        let reading =
                            database.Fixture.Store.ReadCase(
                                database.Fixture.Owner,
                                None,
                                "synthetic-snapshot",
                                token
                            )

                        do! waitForWaiter database

                        let! _ =
                            DatabaseSession.execute
                                holder
                                "UPDATE claimcore.cases SET claimant_name='Changed synthetic'; DELETE FROM claimcore.grants WHERE role='CASE_EDITOR'"
                                [||]
                                token

                        do! transaction.CommitAsync()
                        let! result = reading

                        snapshot result registration.ClaimantName true

                        let! after =
                            database.Fixture.Store.ReadCase(
                                database.Fixture.Owner,
                                None,
                                "synthetic-snapshot",
                                token
                            )

                        snapshot after "Changed synthetic" false
                    }))
        ]
