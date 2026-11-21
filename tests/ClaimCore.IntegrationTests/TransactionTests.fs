module ClaimCore.IntegrationTests.TransactionTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

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
        use! connection = database.RuntimeSource.OpenConnectionAsync()
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
                    let! _, instant = Transactions.head connection token
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
        LedgerStore(database.RuntimeSource, database.Installation, database.Cipher, settings)
        :> IStore

    let result =
        store.Write(
            {
                WriteOperation.Work =
                    (fun _ ->
                        Task.FromResult(
                            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                            : Verdict<unit>
                        ))
                CancellationToken = token
            }
        )

    result

let private snapshot (result: Result<CaseReadFacts, StoreFailure>) name hasHandler =
    match result with
    | Ok {
             Actor = ({ Actor = Some _ } as people)
             Case = Some {
                             Snapshot = {
                                            State = (ClaimCore.Domain.CaseState.Active(fields) | CaseState.Voided(fields))
                                        }
                         }
         } ->
        Expect.equal
            (List.contains Role.CaseHandler people.Installation)
            hasHandler
            "People belongs to this snapshot"

        Expect.equal
            (Identifier.format (Registration.policyNumber (fields.Registration) |> Option.get))
            name
            "Data belongs to this snapshot"
    | _ -> failtest "Expected a coherent read"

[<Tests>]
let tests =
    testList
        "PostgreSQL transactions"
        [
            testCaseAsync
                "concurrent writers decide once on current heads and produce a gapless chain"
                (run (fun database ->
                    task {
                        let! actor = caseWorker database.Fixture

                        let tasks =
                            [|
                                for index in 1..20 ->
                                    (Service.executeSubmission
                                        actor
                                        (((request
                                            (database.Fixture)
                                            ((100 + index))
                                            (($"synthetic-{index}"))
                                            (0L)
                                            "case.open"
                                            (ClaimCore.TestSupport.Submissions.fields (
                                                RegistrationInput.values (registration)
                                            )))))
                                        (token))
                            |]

                        let! results = Task.WhenAll tasks

                        let positions =
                            results
                            |> Array.map (accepted >> _.Position.Sequence >> Sequence.value)
                            |> Array.sort

                        Expect.equal
                            positions
                            [| 4L .. 23L |]
                            "Each locked decision saw the current sequence"

                        let! verified =
                            Verification.verify database.OwnerSource database.MasterKey None token

                        Expect.equal
                            (VerificationSupport.position verified).Sequence
                            (CaseFixtures.sequence (23L))
                            "All links recompute"
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
                        let! actor = caseWorker database.Fixture
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
                            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
                            "Next writer proceeds"

                        let! head =
                            Verification.verify database.OwnerSource database.MasterKey None token

                        Expect.equal
                            (VerificationSupport.position head).Sequence
                            (CaseFixtures.sequence (3L))
                            "Neither waiter wrote"

                        let! _ =
                            (Service.get
                                actor
                                ({
                                    Target = CaseSelector.Reference("absent")
                                })
                                (token))

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
                                                let! head, _ = Transactions.head connection token in

                                                return head.Sequence, false
                                            })

                                Expect.equal
                                    head
                                    (Ok(CaseFixtures.sequence (0L)))
                                    "Next writer obtains released lock"
                    }))
            testCaseAsync
                "deferred commit error is definite and rolls back entry and projection"
                (run (fun database ->
                    task {
                        let! actor = caseWorker database.Fixture

                        do!
                            sql
                                database
                                "CREATE FUNCTION claimcore.reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic deferred refusal' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER reject_commit AFTER INSERT ON claimcore.cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION claimcore.reject_commit()"

                        let operation =
                            request
                                (database.Fixture)
                                (20)
                                ("synthetic-deferred")
                                (0L)
                                "case.open"
                                (ClaimCore.TestSupport.Submissions.fields (
                                    RegistrationInput.values (registration)
                                ))

                        let! failed = (Service.executeSubmission actor ((operation)) (token))

                        Expect.equal
                            failed.Outcome
                            (ExecuteResult.Failed CoreFault.StoreUnavailable)
                            "SQLSTATE is definite"

                        let! head =
                            Verification.verify database.OwnerSource database.MasterKey None token

                        Expect.equal
                            (VerificationSupport.position head).Sequence
                            (CaseFixtures.sequence (3L))
                            "Failed commit did not append"

                        do! sql database "DROP TRIGGER reject_commit ON claimcore.cases"

                        let! retry = (Service.executeSubmission actor ((operation)) (token))

                        accepted retry |> ignore
                    }))
            testCaseAsync
                "append head guard rejects a gap; removing it leaves independently detectable history"
                (run (fun database ->
                    task {
                        let identity = CaseFixtures.operationId (id database.Fixture 900)
                        let target = ActorId.ofOperationId identity


                        let root =
                            RetainedOperation.create
                                {
                                    InstallationId = database.Fixture.InstallationId
                                    Sequence = CaseFixtures.sequence 2L
                                    PreviousHash = EntryHash.zero
                                    OperationId = identity
                                    Authority = Authority.Actor target
                                    RecordedAt =
                                        DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
                                    BusinessDate = None
                                }
                                (TaskId.ofLiteral "people.enable")
                                (Binding.People(target, Revision.first))
                                InputMap.empty
                                [
                                    {
                                        PartChange.Aggregate = AggregateReference.Person target
                                        PartId = ActorId.value target
                                        Revision = Revision.first
                                        NaturalKey = None
                                        Erases = None
                                        Value =
                                            PartValue.Person
                                                {
                                                    Principal = None
                                                    Enabled = true
                                                    Grants = []
                                                    Profile = ProfileValue.Absent
                                                }
                                        Stamp =
                                            {
                                                Position =
                                                    EffectPosition.create
                                                        (CaseFixtures.sequence 2L)
                                                        0
                                                    |> Option.get
                                                OperationId = identity
                                                Authority = Authority.Actor target
                                                RecordedAt =
                                                    DateTimeOffset(
                                                        2026,
                                                        9,
                                                        7,
                                                        12,
                                                        0,
                                                        0,
                                                        TimeSpan.Zero
                                                    )
                                            }
                                    }
                                ]

                                (fun () -> failtest "A public-only operation has no nonce")
                            |> Result.defaultWith (fun error ->
                                failtestf "Public root fixture: %A" error)
                            |> _.Root

                        use! connection = database.RuntimeSource.OpenConnectionAsync()

                        let attempt () =
                            task {
                                try
                                    let! _ =
                                        OperationStorage.append
                                            connection
                                            database.Cipher
                                            root
                                            token

                                    return None
                                with :? PostgresException as error ->
                                    return Some error.SqlState
                            }

                        let! guarded = attempt ()
                        Expect.equal guarded (Some "CC001") "A gap cannot extend the current head"

                        do!
                            sql
                                database
                                "DO $test$ DECLARE original text; changed text; BEGIN SELECT pg_get_functiondef(oid) INTO STRICT original FROM pg_proc WHERE pronamespace='claimcore'::regnamespace AND proname='append_operation'; changed:=replace(original,'IF p_sequence<>head.sequence+1 OR p_previous_hash<>head.entry_hash THEN RAISE EXCEPTION ''operation does not extend the current head'' USING ERRCODE=''CC001''; END IF;',''); IF changed=original THEN RAISE EXCEPTION 'negative control did not remove the head guard'; END IF; EXECUTE changed; END $test$"

                        let! childGuarded = attempt ()

                        Expect.equal
                            childGuarded
                            (Some "CC003")
                            "The independent child guard still refuses the same gap"

                        do!
                            sql
                                database
                                "DROP TRIGGER effects_insert_guard ON claimcore.ledger_effects; DROP TRIGGER compartments_insert_guard ON claimcore.ledger_compartments"

                        let! unguarded = attempt ()

                        Expect.isNone
                            unguarded
                            "Removing both owned structural safeguards admits the same gap"

                        let! finding =
                            Verification.verify database.OwnerSource database.MasterKey None token

                        match finding with
                        | VerificationResult.Diverged _ -> ()
                        | _ -> failtest "Independent verification must reject the accepted gap"
                    }))
            testCaseAsync
                "case data and people remain in one snapshot across a concurrent change"
                (run (fun database ->
                    task {
                        let! actor = caseWorker database.Fixture

                        let! opened =
                            (Service.executeSubmission
                                actor
                                (((request
                                    (database.Fixture)
                                    (20)
                                    ("synthetic-snapshot")
                                    (0L)
                                    "case.open"
                                    (ClaimCore.TestSupport.Submissions.fields (
                                        RegistrationInput.values (registration)
                                    )))))
                                (token))

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
                            database.Fixture.Store.Read
                                {
                                    Work =
                                        fun tx ->
                                            CaseReadContext.load
                                                actor
                                                tx
                                                (CaseSelector.Reference "synthetic-snapshot")
                                    CancellationToken = token
                                }

                        do! waitForWaiter database

                        let! _ =
                            DatabaseSession.execute
                                holder
                                "UPDATE claimcore.cases SET snapshot=convert_to(replace(convert_from(snapshot,'UTF8'), 'Synthetic Policy', 'Changed synthetic'),'UTF8'); DELETE FROM claimcore.grants WHERE role='CASE_HANDLER'"
                                [||]
                                token

                        do! transaction.CommitAsync()
                        let! result = reading

                        snapshot
                            result
                            (RegistrationInput.tryValue BusinessField.PolicyNumber (registration)
                             |> Option.get)
                            true

                        let! after =
                            database.Fixture.Store.Read
                                {
                                    Work =
                                        fun tx ->
                                            CaseReadContext.load
                                                actor
                                                tx
                                                (CaseSelector.Reference "synthetic-snapshot")
                                    CancellationToken = token
                                }

                        snapshot after "Changed synthetic" false
                    }))
        ]
