module ClaimCore.IntegrationTests.MaintenanceFencingPostgresTests

open System
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private pauseA = Guid.Parse "10000000-0000-4000-8000-000000000091"
let private pauseB = Guid.Parse "10000000-0000-4000-8000-000000000092"
let private pauseC = Guid.Parse "10000000-0000-4000-8000-000000000093"

let private handle generation pause =
    MaintenanceHandle.create generation pause |> Option.get

let private change (database: PostgresFixture.Database) action =
    RegisterMaintenanceStore.change database.OwnerSource action token

let private conflict (pending: Task<'a>) =
    task {
        let mutable fault = None

        try
            let! _ = pending
            ()
        with :? OperatorFaultException as error ->
            fault <- Some error.Fault

        Expect.equal
            fault
            (Some OperatorFault.MaintenanceConflict)
            "Stale ownership is an explicit conflict"
    }

let private fencedLifecycle (database: PostgresFixture.Database) =
    task {
        let! acquired = change database (MaintenanceChange.Acquire(0L, pauseA))
        let! retry = change database (MaintenanceChange.Acquire(0L, pauseA))

        Expect.equal
            acquired.State
            (RegisterMaintenance.create 1L (Some pauseA))
            "Acquire establishes one generation"

        Expect.isTrue acquired.Changed "Initial acquire is an actual change"
        Expect.equal retry.State acquired.State "Same-active retry observes the same current handle"
        Expect.isFalse retry.Changed "Same-active retry does not claim another transition"

        let! successor =
            change database (MaintenanceChange.Takeover(handle 1L pauseA, pauseB))

        Expect.equal
            successor.State
            (RegisterMaintenance.create 2L (Some pauseB))
            "Takeover advances ownership"

        do! conflict (change database (MaintenanceChange.Resume(handle 1L pauseA)))
        do! conflict (change database (MaintenanceChange.Takeover(handle 1L pauseA, pauseC)))
        let! status = InstallationStatus.read database.OwnerSource token

        Expect.equal
            status.Maintenance
            successor.State
            "Old operators cannot release or replace a successor"

        let! released = change database (MaintenanceChange.Resume(handle 2L pauseB))

        Expect.equal
            released.State
            (RegisterMaintenance.create 3L None)
            "Resume advances the inactive generation"

        do! conflict (change database (MaintenanceChange.Acquire(0L, pauseA)))
        do! conflict (change database (MaintenanceChange.Resume(handle 2L pauseB)))
        let! status = InstallationStatus.read database.OwnerSource token

        Expect.equal
            status.Maintenance
            released.State
            "Delayed requests cannot recreate a released pause"

        Expect.equal
            status.Position
            LedgerPosition.genesis
            "Maintenance does not append ledger history"
    }

[<RequireQualifiedAccess>]
type private Transition =
    | Acquire
    | Resume
    | Takeover

let private lostAcknowledgement transition drop landed (database: PostgresFixture.Database) =
    task {
        let! initial =
            match transition with
            | Transition.Acquire -> Task.FromResult RegisterMaintenance.initial
            | Transition.Resume
            | Transition.Takeover ->
                task {
                    let! result = change database (MaintenanceChange.Acquire(0L, pauseA))
                    return result.State
                }

        let requested, after =
            match transition with
            | Transition.Acquire ->
                MaintenanceChange.Acquire(0L, pauseA), RegisterMaintenance.create 1L (Some pauseA)
            | Transition.Resume ->
                MaintenanceChange.Resume(handle 1L pauseA), RegisterMaintenance.create 2L None
            | Transition.Takeover ->
                MaintenanceChange.Takeover(handle 1L pauseA, pauseB),
                RegisterMaintenance.create 2L (Some pauseB)

        let settings =
            NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnectionString database)

        use proxy = new CommitProxy.Proxy(nonNull settings.Host, settings.Port, drop)
        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        settings.Pooling <- false
        use source = NpgsqlDataSource.Create settings.ConnectionString
        let mutable unknown = false

        try
            let! _ = RegisterMaintenanceStore.change source requested token
            ()
        with :? AdministrationOutcomeUnknownException ->
            unknown <- true

        Expect.isTrue unknown "Real lost commit acknowledgement never reports historical success"

        Expect.isTrue
            proxy.Intercepted.IsCompleted
            "The protocol relay reached the actual commit boundary"

        let! status = InstallationStatus.read database.OwnerSource token

        Expect.equal
            status.Maintenance
            (if landed then after else initial)
            "Fresh status reports actual current state only"

        Expect.equal
            status.Position
            LedgerPosition.genesis
            "Lost acknowledgements create no ledger proof"

        if landed then
            match transition with
            | Transition.Acquire ->
                let! retry = change database requested

                Expect.isFalse
                    retry.Changed
                    "Retry of a currently active acquire reports an observation, not a new transition"

                let! _ = change database (MaintenanceChange.Resume(handle 1L pauseA))
                do! conflict (change database requested)
            | Transition.Resume
            | Transition.Takeover -> do! conflict (change database requested)
        else
            let! retry = change database requested

            Expect.isTrue
                retry.Changed
                "After explicit status inspection the still-current request can be issued as a new attempt"

            Expect.equal retry.State after "Confirmed explicit retry makes the intended transition"
    }

let private snapshot (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT jsonb_build_object('actors',(SELECT jsonb_agg(to_jsonb(a) ORDER BY actor_id) FROM claimcore.actors a),'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY granted_sequence,granted_effect_index) FROM claimcore.grants g),'cases',(SELECT jsonb_agg(to_jsonb(c) ORDER BY case_id) FROM claimcore.cases c),'work',(SELECT jsonb_agg(to_jsonb(w) ORDER BY case_id) FROM claimcore.case_work w),'notes',(SELECT jsonb_agg(to_jsonb(n) ORDER BY note_id) FROM claimcore.notes n),'payments',(SELECT jsonb_agg(to_jsonb(p) ORDER BY payment_id) FROM claimcore.case_payments p),'reminders',(SELECT jsonb_agg(to_jsonb(r) ORDER BY reminder_id) FROM claimcore.reminders r),'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY session_key) FROM claimcore.sessions s),'head',(SELECT to_jsonb(h) FROM claimcore.ledger_head h))::text"
                [||]
                token

        return rows |> List.exactlyOne |> DatabaseSession.value<string> 0
    }

let private waitForHeadWaiter (database: PostgresFixture.Database) application blocker =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let timer = Diagnostics.Stopwatch.StartNew()
        let mutable blocked = false

        while not blocked && timer.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock' AND $2=ANY(pg_blocking_pids(pid)))"
                    [| box application; box blocker |]
                    token

            blocked <- rows |> List.exactlyOne |> DatabaseSession.value<bool> 0

            if not blocked then
                do! Task.Delay 10

        Expect.isTrue
            blocked
            "The real owner command queued behind this transaction's common head lock before ownership changed"
    }

let private queuedCommand rebuild (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let! trusted = checkpoint database
        let! _ = change database (MaintenanceChange.Acquire(0L, pauseA))
        // A stale rebuild would repair this real divergence; stale restore would advance expiry and delete this ticket.
        do!
            sql
                database
                "UPDATE claimcore.actors SET display_name='Synthetic projection divergence' WHERE actor_id=$1"
                [| box (id database.Fixture 1) |]

        let principal =
            PrincipalKey.human "https://issuer.example/" "fenced-session"
            |> Result.defaultWith (string >> failtest)

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! _ =
            store.Create(
                principal,
                [| 1uy |],
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        let! before = snapshot database
        let future = DateTimeOffset.UtcNow.AddHours 1.

        let restored =
            InstallationCheckpoint.create trusted.InstallationId trusted.Position (Some future)
            |> Option.get

        let settings =
            NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnectionString database)

        settings.ApplicationName <- if rebuild then "fenced-rebuild" else "fenced-restore"
        use commandSource = NpgsqlDataSource.Create settings.ConnectionString
        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()
        let! _ = Transactions.head holder token
        // The old handle is genuinely active when the command starts and reaches the held head lock.
        let pending =
            if rebuild then
                task {
                    let! _ =
                        ProjectionRebuild.rebuild commandSource trusted (handle 1L pauseA) token

                    return ()
                }
            else
                task {
                    let! _ = RestoreAdmission.admit commandSource restored (handle 1L pauseA) token
                    return ()
                }

        do! waitForHeadWaiter database settings.ApplicationName holder.ProcessID

        let! successor, changed =
            RegisterMaintenanceStore.changeWithinHeadLock
                holder
                (MaintenanceChange.Takeover(handle 1L pauseA, pauseB))
                token

        Expect.isTrue
            changed
            "The lock holder changes ownership through the production transition owner"

        do! transaction.CommitAsync()
        do! conflict pending
        let! after = snapshot database

        Expect.equal
            after
            before
            "Queued stale work changes no projection, ticket, expiry frontier or ledger head"

        let! status = InstallationStatus.read database.OwnerSource token

        Expect.equal
            status.Maintenance
            successor
            "The successor handle remains active after the stale command acquires serialization"
        // A fresh command with the exact successor handle proves the earlier refusal was fencing, not unusable input.
        if rebuild then
            let! result =
                ProjectionRebuild.rebuild commandSource trusted (handle 2L pauseB) token

            Expect.isOk result "The actual current handle can rebuild the same valid history"
            let! _ = verified database
            ()
        else
            let! result = RestoreAdmission.admit commandSource restored (handle 2L pauseB) token
            let report = result |> Result.defaultWith (string >> failtest)

            Expect.equal
                report.InvalidatedSessions
                1L
                "The accepted current command really invalidates the stored ticket"

            Expect.equal
                report.Checkpoint.ExpiredBefore
                (Some future)
                "The accepted current command really advances the frontier"
    }

[<Tests>]
let tests =
    testList
        "real maintenance fencing"
        [
            testCaseAsync
                "same-active retry delayed acquire and stale owners respect successor generations"
                (run fencedLifecycle)
            for transition in [ Transition.Acquire; Transition.Resume; Transition.Takeover ] do
                for drop, landed in
                    [ CommitProxy.Drop.BeforeCommit, false; CommitProxy.Drop.AfterCommit, true ] do
                    testCaseAsync
                        (sprintf "%A %A reports uncertainty and only current status" transition drop)
                        (run (lostAcknowledgement transition drop landed))
            testCaseAsync
                "queued rebuild refuses an old handle after actual head acquisition"
                (run (queuedCommand true))
            testCaseAsync
                "queued restored admission refuses an old handle after actual head acquisition"
                (run (queuedCommand false))
        ]
