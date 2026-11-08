module ClaimCore.IntegrationTests.PreparedReadTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private succeeded =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "%A" other

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject |> required

let private input principal =
    let kind, issuer, subject = PrincipalKey.parts principal in

    {
        PrincipalInput.Kind = kind
        Issuer = issuer
        Subject = subject
    }

let private withPool run =
    async {
        let! database =
            PostgresFixture.createForPool (principal "prepared-owner") 1 |> Async.AwaitTask

        try
            do! run database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private freshness (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture
        let target = CaseSelector.Reference "PREPARED-CASE"

        let! opened =
            Service.execute
                admin
                (Inputs.caseOperation (
                    request
                        database.Fixture
                        100
                        "PREPARED-CASE"
                        0L
                        (AuthoredCaseAct.Open registration)
                ))
                token

        let caseId = accepted opened |> caseReceipt |> _.CaseId
        let human = principal "prepared-scoped"

        let service =
            PrincipalKey.service "https://issuer.example/" "prepared-service" |> required

        let roleless = principal "prepared-roleless"

        for operation, who in [ 110, human; 111, service; 112, roleless ] do
            let! added =
                Service.execute
                    admin
                    (OperationInput.Authority(
                        authorityRequest
                            (id database.Fixture operation)
                            who
                            (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                    ))
                    token

            acceptedAuthority added

        for operation, who in [ 120, human; 121, service ] do
            let! added =
                grant
                    admin
                    (id database.Fixture operation)
                    who
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseId caseId
                    }
                    true

            acceptedAuthority added

        let facts who =
            database.Fixture.Store.Read
                {
                    Work = (fun tx -> tx.GlobalReadFacts who)
                    CancellationToken = token
                }

        for _ in 1..7 do
            for who, expected in
                [ database.Fixture.Owner, false; human, true; service, true; roleless, false ] do
                let! result = facts who
                let result = required result
                Expect.isSome result.Authority.Actor "Zero-grant and service actors remain present"

                Expect.equal
                    result.Authority.HasScopedCases
                    expected
                    "Prepared global facts bind the current principal and role arrays"

                Expect.isFalse
                    result.Maintenance.Active
                    "Current operational owner remains independent"

        let unknown = principal "prepared-unknown"
        let! absent = facts unknown

        Expect.equal
            (required absent).Authority
            AuthorityView.empty
            "An absent actor does not become a NULL pseudo-actor"

        let! planned =
            Service.execute
                admin
                (OperationInput.Reminder
                    {
                        OperationId = id database.Fixture 130
                        Target = target
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Current prepared action"
                                    Due = "2026-11-02"
                                }
                    })
                token

        accepted planned |> ignore

        let query date =
            database.Fixture.Store.Read
                {
                    Work = (fun tx -> tx.WorkSummaries([ caseId ], date))
                    CancellationToken = token
                }

        use! before = database.OwnerSource.OpenConnectionAsync()

        let! headBefore =
            DatabaseSession.rows
                before
                "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                [||]
                token

        for _ in 1..7 do
            for date, overdue in
                [
                    DateOnly(2026, 11, 1), 0L
                    DateOnly(2026, 11, 2), 0L
                    DateOnly(2026, 11, 3), 1L
                ] do
                let! summaries = query date
                let value = (required summaries)[caseId]

                Expect.equal
                    (value.Pending, value.Overdue)
                    (1L, overdue)
                    "A warmed statement rebinds business dates without changing reminder state"

                Expect.equal
                    (value.Next |> Option.map (Reminder.action >> ReminderAction.format))
                    (Some "Current prepared action")
                    "The canonical reminder payload remains exact"

        let! headAfter =
            DatabaseSession.rows
                before
                "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                [||]
                token

        Expect.equal headAfter headBefore "Read date changes never write the ledger"

        let! disabled =
            Service.execute
                admin
                (OperationInput.Authority(
                    authorityRequest (id database.Fixture 140) human AuthorityAct.Disable
                ))
                token

        acceptedAuthority disabled
        let! off = facts human

        Expect.equal
            ((required off).Authority.Actor |> Option.map _.Enabled)
            (Some false)
            "Prepared execution observes a post-cache disable"

        Expect.isTrue
            (required off).Authority.HasScopedCases
            "Scoped presence is still a fact for a disabled actor"

        let! revoked =
            grant
                admin
                (id database.Fixture 141)
                human
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId caseId
                }
                false

        acceptedAuthority revoked
        let! removed = facts human

        Expect.isFalse
            (required removed).Authority.HasScopedCases
            "Prepared execution observes a post-cache grant removal"

        use! pooled = database.RuntimeSource.OpenConnectionAsync()
        let physical = pooled.ProcessID

        let! statements =
            DatabaseSession.rows
                pooled
                "SELECT statement,generic_plans+custom_plans FROM pg_prepared_statements ORDER BY statement"
                [||]
                token

        Expect.equal
            statements.Length
            2
            "Only the two fixed read statements are prepared on this physical connection"

        Expect.isTrue
            (statements |> List.forall (fun row -> DatabaseSession.value<int64> 1 row > 5L))
            "Natural repeated use exercises server plan reuse"

        Expect.isTrue
            (statements
             |> List.exists (fun row ->
                 (DatabaseSession.value<string> 0 row)
                     .StartsWith("WITH current_mode", StringComparison.Ordinal)))
            "The global fact query is the prepared positional consumer"

        Expect.isTrue
            (statements
             |> List.exists (fun row ->
                 (DatabaseSession.value<string> 0 row)
                     .StartsWith("SELECT w.case_id", StringComparison.Ordinal)))
            "The bounded work query is the prepared named consumer"

        do! pooled.CloseAsync()
        do! pooled.OpenAsync(token)

        Expect.equal
            pooled.ProcessID
            physical
            "Ordinary pool close/reopen reuses the physical connection"

        let! preserved =
            DatabaseSession.rows pooled "SELECT count(*) FROM pg_prepared_statements" [||] token

        Expect.equal
            (DatabaseSession.value<int64> 0 preserved.Head)
            2L
            "Pool reset preserves the bounded fixed statement set"
    }

let private multiplexing (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! opened =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request
                        database.Fixture
                        500
                        "MULTIPLEXED-WORK"
                        0L
                        (AuthoredCaseAct.Open registration)
                ))
                token

        let caseId = accepted opened |> caseReceipt |> _.CaseId

        let settings =
            NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

        settings.Password <- "synthetic-only"
        settings.Multiplexing <- true
        use source = NpgsqlDataSource.Create(settings.ConnectionString)
        use! connection = source.OpenConnectionAsync()
        let! facts = Projections.globalReadFacts connection database.Fixture.Owner token

        Expect.isSome
            facts.Authority.Actor
            "An existing multiplexed configuration retains unprepared global reads"

        let! work = WorkStorage.summaries connection [ caseId ] (DateOnly(2026, 11, 2)) token

        Expect.equal
            work.Count
            1
            "Multiplexed named-row work reads use their supported unprepared path"

        Expect.equal work[caseId].Work.Revision Revision.zero "The current root remains genuine"
    }

let private cancellation (database: PostgresFixture.Database) =
    task {
        let! _ = caseWorker database.Fixture

        let settings =
            NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

        settings.Password <- "synthetic-only"
        settings.Pooling <- true
        settings.MaxPoolSize <- 1
        settings.MinPoolSize <- 0
        let applicationName = "claimcore-prepared-cancel-" + Guid.NewGuid().ToString("N")
        settings.ApplicationName <- applicationName
        use source = NpgsqlDataSource.Create(settings.ConnectionString)

        let store =
            LedgerStore(source, database.Fixture.InstallationId, Transactions.timeouts) :> IStore

        let core =
            Service.create
                "prepared-cancellation"
                database.Fixture.InstallationId
                store
                database.Fixture.Calendar
                database.Fixture.Owner

        use! owner = database.OwnerSource.OpenConnectionAsync()
        use! locked = owner.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                owner
                "LOCK TABLE claimcore.actors IN ACCESS EXCLUSIVE MODE"
                [||]
                token

        use cancel = new CancellationTokenSource()
        let reading = Service.context core () cancel.Token
        use! observer = database.OwnerSource.OpenConnectionAsync()
        let watch = Stopwatch.StartNew()
        let mutable blocked = false

        while not blocked && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    observer
                    "SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND datname=current_database() AND wait_event_type='Lock' AND query LIKE '%current_mode%'"
                    [| box applicationName |]
                    token

            blocked <- DatabaseSession.value<int64> 0 rows.Head > 0L

            if not blocked then
                do! Task.Delay 10

        Expect.isTrue
            blocked
            "A fresh fixed statement is actually blocked in preparation or execution before cancellation"

        cancel.Cancel()
        let! outcome = reading

        Expect.equal
            outcome
            (Outcome.Failed CoreFault.StoreUnavailable)
            "Cancellation before a read outcome remains a generic unavailable result"

        do! locked.RollbackAsync(token)
        let! recovered = (Service.context core () token).WaitAsync(TimeSpan.FromSeconds 3.)

        Expect.equal
            (Inputs.activeContext (succeeded recovered)).ActorId
            (CaseFixtures.actorId (id database.Fixture 1))
            "A fresh request succeeds after cancellation cleanup"
    }

[<Tests>]
let tests =
    testList
        "fixed prepared read statements"
        [
            testCaseAsync
                "current principal dates and facts survive warmed plans and pool reuse"
                (withPool freshness)
            testCaseAsync
                "multiplexed sources retain supported unprepared reads"
                (withPool multiplexing)
            testCaseAsync
                "cancelled cold preparation releases its connection for recovery"
                (withPool cancellation)
        ]
