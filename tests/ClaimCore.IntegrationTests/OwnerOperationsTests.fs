module ClaimCore.IntegrationTests.OwnerOperationsTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private success =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected success: %A" other

let private changed (database: PostgresFixture.Database) change =
    task {
        let! result = RegisterMaintenanceStore.change database.OwnerSource change token

        return
            result
            |> Result.defaultWith (fun actual -> failtestf "Unexpected owner conflict: %A" actual)
    }

let private statusAndGenerations (database: PostgresFixture.Database) =
    task {
        let! initial = InstallationStatus.read database.OwnerSource token

        Expect.equal
            initial.InstallationId
            database.Fixture.InstallationId
            "Exact initialized installation"

        Expect.equal initial.BusinessTimeZone "Etc/UTC" "Immutable installation zone"

        Expect.equal
            initial.Formats
            {
                CaseRequest = 4
                Snapshot = 4
                Authority = 3
                LedgerEntry = 6
                Work = 1
                Reminder = 1
            }
            "Independent supported format inventory"

        Expect.equal
            initial.Maintenance
            RegisterMaintenance.initial
            "Fresh installation is writable at generation zero"

        Expect.equal
            (initial.Actors, initial.Cases, initial.Sessions)
            (0L, 0L, 0L)
            "Read-only empty resource inventory"

        let! entered = changed database MaintenanceChange.Enter
        Expect.isTrue entered.Changed "First entry changes operational state"

        Expect.equal
            (entered.State.Active, entered.State.Generation)
            (true, 1L)
            "First active generation"

        let! repeated = changed database MaintenanceChange.Enter
        Expect.isFalse repeated.Changed "Repeated entry observes the same active interval"
        Expect.equal repeated.State entered.State "Repeated entry does not advance generation"

        let! provisioned =
            Service.provisionOwner
                (core database.Fixture database.Fixture.Owner)
                (id database.Fixture 1)
                ClaimCore.TestSupport.Inputs.ownerProfile
                token

        match provisioned with
        | Ok(Verdict.Refuse Refusal.RegisterMaintenance) -> ()
        | other -> failtestf "Maintenance must block fresh owner provisioning: %A" other

        let! resumed = changed database (MaintenanceChange.Resume 1L)

        Expect.equal
            (resumed.State.Active, resumed.State.Generation)
            (false, 1L)
            "Resume retains the completed generation"

        let! next = changed database MaintenanceChange.Enter
        Expect.equal next.State.Generation 2L "A new interval advances generation"

        let! stale =
            RegisterMaintenanceStore.change database.OwnerSource (MaintenanceChange.Resume 1L) token

        Expect.equal
            (stale |> Result.map _.State |> Result.mapError _.State)
            (Error next.State)
            "An old resume cannot reopen a later interval"

        let! final = InstallationStatus.read database.OwnerSource token

        Expect.equal
            final.Position
            initial.Position
            "Operational mode and status never append business history"

        Expect.equal final.Actors 0L "Refused provisioning leaves no actor"
    }

let private actionState (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let pending =
            Inputs.caseOperation (
                request database.Fixture 10 "MAINTENANCE-A" 0L (CaseAct.Open registration)
            )

        let! opening = Service.execute actor pending token
        accepted opening |> ignore

        let close =
            Inputs.caseOperation (
                request database.Fixture 11 "MAINTENANCE-A" 1L (CaseAct.Bare BareAct.Close)
            )

        let! prepared = Service.preview actor close token

        match prepared.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Positive pre-maintenance preview: %A" other

        let! before = InstallationStatus.read database.OwnerSource token
        let! _ = changed database MaintenanceChange.Enter
        let! refused = Service.execute actor close token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "A stale preview cannot bypass current action-state maintenance"

        let target =
            PrincipalKey.human "https://issuer.example/" "maintenance-target"
            |> Result.defaultWith (string >> failtest)

        let! authority =
            Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest
                        (id database.Fixture 12)
                        target
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                token

        Expect.equal
            authority.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "Authority writes share the actual gate"

        let expired = OperationIds.at (DateTimeOffset.UtcNow.AddMinutes(-11.))
        let! final = Service.operationGet actor { OperationId = expired } token

        match final.Outcome with
        | Outcome.Succeeded OperationLookupResult.NotAccepted -> ()
        | other -> failtestf "Maintenance allows locked confirmation without append: %A" other

        let! pendingLookup =
            Service.operationGet actor { OperationId = id database.Fixture 13 } token

        match pendingLookup.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Pending _) -> ()
        | other -> failtestf "Fresh absence stays pending during maintenance: %A" other

        let! read =
            Service.get
                actor
                {
                    Target = CaseSelector.Reference "MAINTENANCE-A"
                }
                token

        let read = success read
        Expect.isTrue read.Advice.Maintenance.Active "Case advice reports its observed mode"
        Expect.isEmpty read.Advice.Acts "Current advice offers no register writes"
        let! context = Service.context actor () token
        let context = Inputs.activeContext (success context)
        Expect.isTrue context.Maintenance.Active "Context exposes register maintenance"
        Expect.isEmpty context.Offers "Context offers are read-only during maintenance"
        let! replay = Service.execute actor pending token

        let replayed =
            match replay.Outcome with
            | ExecuteResult.AlreadyAccepted receipt -> receipt
            | other -> failtestf "Expected retained acceptance during maintenance: %A" other

        let original = accepted opening

        Expect.equal
            (replayed.OperationId,
             replayed.Position,
             replayed.Subject,
             replayed.RecordedAt,
             replayed.RecordedBy.ActorId)
            (original.OperationId,
             original.Position,
             original.Subject,
             original.RecordedAt,
             original.RecordedBy.ActorId)
            "Retained acceptance preserves its identity, facts and original recording metadata"

        Expect.isGreaterThanOrEqual
            replayed.Basis.ObservedAt
            context.Runtime.Basis.ObservedAt
            "Replay reports its current authorized observation, after the preceding context read"

        Expect.equal
            (replayed.Basis.BusinessDate, replayed.Basis.TimeZoneId)
            (context.Runtime.Basis.BusinessDate, "Etc/UTC")
            "Replay observation uses the installation business calendar"

        let sessions = SessionStore(database.RuntimeSource) :> ISessionStore

        let! session =
            sessions.Create([| 1uy |], None, TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., token)

        let! ticket = sessions.Retrieve(session.Value, token)

        Expect.equal
            ticket.Value
            (Some [| 1uy |])
            "Operational authentication storage remains available"

        let! after = InstallationStatus.read database.OwnerSource token

        Expect.equal
            after.Position
            before.Position
            "Refusals, passive status, replay and authentication append nothing"

        let! _ = changed database (MaintenanceChange.Resume 1L)
        let! recorded = Service.execute actor close token

        Expect.equal
            (accepted recorded |> caseReceipt |> _.Revision)
            (CaseFixtures.revision 2L)
            "The same refused identity can be explicitly submitted after resume"
    }

let private rebuildMode (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let! checkpoint = verified database
        let mutable refused = false

        try
            let! _ = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
            ()
        with :? MaintenanceRequiredException ->
            refused <- true

        Expect.isTrue
            refused
            "Owner credentials alone cannot silently enter maintenance for rebuild"

        do! enterMaintenance database
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk rebuilt "Maintenance plus the trusted checkpoint permits atomic reconstruction"
        let! after = InstallationStatus.read database.OwnerSource token
        Expect.isTrue after.Maintenance.Active "Reconstruction never auto-resumes"
        Expect.equal after.Position checkpoint "Reconstruction preserves the ledger head"
    }

let private pruning (database: PostgresFixture.Database) =
    task {
        let sessions = SessionStore(database.RuntimeSource) :> ISessionStore

        let! live =
            sessions.Create([| 1uy |], None, TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., token)

        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800 FROM generate_series(1,7) n"
                [||]

        let! before = InstallationStatus.read database.OwnerSource token

        Expect.equal
            (before.Sessions, before.ExpiredSessions)
            (8L, 7L)
            "Independent live and expired population"

        let! report = SessionPruning.prune database.OwnerSource 2 token

        Expect.equal
            (report.Limit, report.ExpiredAtObservation, report.Deleted)
            (2, 7L, 2L)
            "Pruning deletes only the bounded locked batch"

        let! returned = sessions.Retrieve(live.Value, token)
        Expect.equal returned.Value (Some [| 1uy |]) "Live ticket remains available"
        let! after = InstallationStatus.read database.OwnerSource token

        Expect.equal
            (after.Sessions, after.ExpiredSessions)
            (6L, 5L)
            "Actual stored counts agree after deletion"

        Expect.equal
            after.Position
            before.Position
            "Session pruning is independent of ledger ordering"

    }

let private entryWaitsForAcceptance (database: PostgresFixture.Database) =
    task {
        let! actor = populated database

        let staged =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let store =
            { new IStore with
                member _.Context query = database.Fixture.Store.Context query
                member _.Read operation = database.Fixture.Store.Read operation

                member _.Write operation =
                    database.Fixture.Store.Write
                        { operation with
                            Work =
                                fun tx ->
                                    task {
                                        let! verdict = operation.Work tx
                                        staged.TrySetResult() |> ignore
                                        do! release.Task
                                        return verdict
                                    }
                        }
            }

        let delayed = { actor with Store = store }

        let writing =
            Service.execute
                delayed
                (Inputs.caseOperation (
                    request database.Fixture 11 "synthetic-a" 1L (CaseAct.Bare BareAct.Close)
                ))
                token

        do! staged.Task.WaitAsync(TimeSpan.FromSeconds 3.)

        let entering =
            RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.Enter token

        try
            use! observer = database.OwnerSource.OpenConnectionAsync()
            let watch = Diagnostics.Stopwatch.StartNew()
            let mutable waiting = false

            while not waiting && watch.Elapsed < TimeSpan.FromSeconds 3. do
                let! rows =
                    DatabaseSession.rows
                        observer
                        "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE '%lock_ledger_head%')"
                        [||]
                        token

                waiting <- DatabaseSession.value<bool> 0 rows.Head

                if not waiting then
                    do! Task.Delay 10

            Expect.isTrue
                waiting
                "Maintenance is independently observed waiting on the accepted writer's head lock"

            Expect.isFalse
                entering.IsCompleted
                "Uncommitted acceptance cannot be bypassed by maintenance entry"
        finally
            release.TrySetResult() |> ignore

        let! written = writing
        let receipt = accepted written
        let! entered = entering

        match entered with
        | Ok value ->
            Expect.equal
                value.Position
                receipt.Position
                "Entry observes the prior acceptance's committed head"
        | Error _ -> failtest "Maintenance entry must succeed after writer commit"

        let! paused =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request database.Fixture 12 "synthetic-a" 2L (CaseAct.Bare BareAct.Reopen)
                ))
                token

        Expect.equal
            paused.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "The next actual writer observes maintenance"
    }

let private pruneConcurrency (database: PostgresFixture.Database) =
    task {
        let sessions = SessionStore(database.RuntimeSource) :> ISessionStore

        let! live =
            sessions.Create([| 1uy |], None, TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., token)

        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800 FROM generate_series(1,8) n"
                [||]

        let first = SessionPruning.prune database.OwnerSource 4 token
        let second = SessionPruning.prune database.OwnerSource 4 token
        let renewal = sessions.Renew(live.Value, [| 2uy |], token)
        let logout = sessions.Remove(live.Value, SessionEndReason.Logout, token)
        let! batches = Task.WhenAll(first, second)
        do! Task.WhenAll(renewal :> Task, logout :> Task)

        Expect.equal
            (batches |> Array.sumBy _.Deleted)
            8L
            "Concurrent owner batches delete disjoint expired rows"

        let! ticket = sessions.Retrieve(live.Value, token)
        Expect.isNone ticket.Value "Concurrent renewal cannot resurrect a logged-out ticket"
        let! status = InstallationStatus.read database.OwnerSource token

        Expect.equal
            (status.Sessions, status.ExpiredSessions)
            (0L, 0L)
            "Actual resource counts reconcile without deleting history or keys"
    }

[<Tests>]
let tests =
    testList
        "owner operating lifecycle"
        [
            testCaseAsync
                "maintenance entry waits for a prior accepted transaction"
                (run entryWaitsForAcceptance)
            testCaseAsync
                "concurrent pruning renewal and logout retain their action-state rules"
                (run pruneConcurrency)
            testCaseAsync
                "status and maintenance generations are persistent and read-scoped"
                (run statusAndGenerations)
            testCaseAsync
                "maintenance gates writes and permits locked operation confirmation"
                (run actionState)
            testCaseAsync
                "projection reconstruction requires explicit maintenance"
                (run rebuildMode)
            testCaseAsync
                "expired session pruning is bounded and preserves live tickets and history"
                (run pruning)
        ]
