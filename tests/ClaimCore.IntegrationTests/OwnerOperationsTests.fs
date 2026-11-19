module ClaimCore.IntegrationTests.OwnerOperationsTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Administration
open ClaimCore.Wire
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private success =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected success: %A" other

let private changed (database: PostgresFixture.Database) change =
    RegisterMaintenanceStore.change database.OwnerSource change token

let private statusAndModes (database: PostgresFixture.Database) =
    task {
        let! initial = InstallationStatus.read database.OwnerSource token
        Expect.equal initial.InstallationId database.Fixture.InstallationId "Exact installation"
        Expect.equal initial.BusinessTimeZone "Etc/UTC" "Immutable zone"
        Expect.equal initial.InstallationFormat 8 "Fresh installations use the consolidated grammar"
        Expect.isFalse initial.Maintenance.Active "Fresh schema is writable"

        Expect.isNone
            initial.InitialOwner
            "The internal unprovisioned fixture has no invented owner"

        let! on =
            changed
                database
                (MaintenanceChange.Acquire(
                    0L,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                ))

        Expect.isTrue on.State.Active "On pauses register writes"
        Expect.isTrue on.Changed "First on changes mode"

        let! repeated =
            changed
                database
                (MaintenanceChange.Acquire(
                    0L,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                ))

        Expect.isFalse repeated.Changed "On is idempotent"

        let! provisioned =
            Service.provisionOwner
                (core database.Fixture database.Fixture.Owner)
                (id database.Fixture 1)
                DomainInputs.ownerProfile
                []
                token

        Expect.equal
            provisioned
            (Ok(Verdict.Refuse Refusal.RegisterMaintenance))
            "Paused schema cannot record an owner"

        let! off =
            changed
                database
                (MaintenanceChange.Resume(
                    MaintenanceHandle.create
                        1L
                        (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                    |> Option.get
                ))

        Expect.isFalse off.State.Active "Off reopens writes"
        Expect.isTrue off.Changed "First off changes mode"
        let mutable stale = false

        try
            let! _ = changed database (MaintenanceChange.Resume(on.State.Handle |> Option.get))
            ()
        with :? OperatorFaultException as error ->
            stale <- error.Fault = OperatorFault.MaintenanceConflict

        Expect.isTrue stale "Repeated old resume has no historical success claim"

        let! next =
            changed
                database
                (MaintenanceChange.Acquire(
                    off.State.Generation,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000098"
                ))

        Expect.equal next.State.Generation 3L "Later acquisition advances the generation"

        let! finalOff =
            changed database (MaintenanceChange.Resume(next.State.Handle |> Option.get))

        Expect.isFalse finalOff.State.Active "Only the exact later handle resumes"
        let! final = InstallationStatus.read database.OwnerSource token

        Expect.equal
            final.Position
            initial.Position
            "Operational mode does not append business history"

        Expect.equal final.Actors 0L "Refused provisioning left no actor"
    }

let private actionState (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let pending =
            ((request
                (database.Fixture)
                (10)
                ("MAINTENANCE-A")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration)))))

        let! opening = Service.executeSubmission actor pending token
        accepted opening |> ignore

        let close =
            ((request
                (database.Fixture)
                (11)
                ("MAINTENANCE-A")
                (1L)
                "case.close"
                (CaseTransitionInputs.closed)))

        let! prepared = Service.previewSubmission actor close token

        match prepared.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Positive pre-maintenance preview: %A" other

        let! before = InstallationStatus.read database.OwnerSource token

        let! _ =
            changed
                database
                (MaintenanceChange.Acquire(
                    0L,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                ))

        let! refused = Service.executeSubmission actor close token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "A stale preview cannot bypass current action-state maintenance"

        let target =
            PrincipalKey.human "https://issuer.example/" "maintenance-target"
            |> Result.defaultWith (string >> failtest)

        let! people =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 12))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission (token)
            })

        Expect.equal
            people.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "People writes share the actual gate"

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
        let! replay = Service.executeSubmission actor pending token

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
             replayed.RecordedBy)
            (original.OperationId,
             original.Position,
             original.Subject,
             original.RecordedAt,
             original.RecordedBy)
            "Retained acceptance preserves its identity, facts and original recording metadata"

        Expect.isGreaterThanOrEqual
            replayed.Basis.ObservedAt
            context.Runtime.Basis.ObservedAt
            "Replay reports its current authorized observation, after the preceding context read"

        Expect.equal
            (replayed.Basis.BusinessDate, replayed.Basis.TimeZoneId)
            (context.Runtime.Basis.BusinessDate, "Etc/UTC")
            "Replay observation uses the installation business calendar"

        let sessions =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! session =
            sessions.Create(
                SessionResults.principal,
                [| 1uy |],
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        let! ticket = sessions.Retrieve(session.Value, token)

        Expect.equal
            (ticket.Value |> Option.map _.Payload)
            (Some [| 1uy |])
            "Operational authentication storage remains available"

        let! after = InstallationStatus.read database.OwnerSource token

        Expect.equal
            after.Position
            before.Position
            "Refusals, passive status, replay and authentication append nothing"

        let! _ =
            changed
                database
                (MaintenanceChange.Resume(
                    MaintenanceHandle.create
                        1L
                        (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                    |> Option.get
                ))

        let! recorded = Service.executeSubmission actor close token

        Expect.equal
            (accepted recorded |> caseReceipt |> _.Revision)
            (CaseFixtures.revision 2L)
            "The same refused identity can be explicitly submitted after resume"
    }

let private rebuildMode (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let! checkpoint = checkpoint database
        let mutable refused = false

        try
            let! _ =
                ProjectionRebuild.rebuild
                    database.OwnerSource
                    checkpoint
                    (MaintenanceHandle.create
                        1L
                        (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                     |> Option.get)
                    token

            ()
        with :? MaintenanceRequiredException ->
            refused <- true

        Expect.isTrue
            refused
            "Owner credentials alone cannot silently enter maintenance for rebuild"

        do! enterMaintenance database

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Maintenance plus the trusted checkpoint permits atomic reconstruction"
        let! after = InstallationStatus.read database.OwnerSource token
        Expect.isTrue after.Maintenance.Active "Reconstruction never auto-resumes"
        Expect.equal after.Position checkpoint.Position "Reconstruction preserves the ledger head"
    }

let private pruning (database: PostgresFixture.Database) =
    task {
        let sessions =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! live =
            sessions.Create(
                SessionResults.principal,
                [| 1uy |],
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800,repeat('b',64) FROM generate_series(1,7) n"
                [||]

        let! before = InstallationStatus.read database.OwnerSource token

        Expect.equal
            (before.Sessions, before.ExpiredSessions)
            (8L, 7L)
            "Independent live and expired population"

        let! report =
            Housekeeping.prune
                database.OwnerSource
                database.Installation
                2
                (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                token

        Expect.equal report.SessionsDeleted 2L "Housekeeping deletes only the bounded locked batch"
        Expect.equal report.AccessRequestsDeleted 0L "No access hints were expired"

        let! returned = sessions.Retrieve(live.Value, token)

        Expect.equal
            (returned.Value |> Option.map _.Payload)
            (Some [| 1uy |])
            "Live ticket remains available"

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
            Service.executeSubmission
                delayed
                (((request
                    (database.Fixture)
                    (11)
                    ("synthetic-a")
                    (1L)
                    "case.close"
                    (CaseTransitionInputs.closed))))
                token

        do! staged.Task.WaitAsync(TimeSpan.FromSeconds 3.)

        let entering =
            RegisterMaintenanceStore.change
                database.OwnerSource
                (MaintenanceChange.Acquire(
                    0L,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                ))
                token

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

        Expect.equal
            entered.Position
            receipt.Position
            "Maintenance observes the previous acceptance's committed head"

        let! paused =
            Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (12)
                    ("synthetic-a")
                    (2L)
                    "case.reopen"
                    (CaseTransitionInputs.reopened))))
                token

        Expect.equal
            paused.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "The next actual writer observes maintenance"
    }

let private pruneConcurrency (database: PostgresFixture.Database) =
    task {
        let sessions =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! live =
            sessions.Create(
                SessionResults.principal,
                [| 1uy |],
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        do!
            sql
                database
                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key) SELECT lpad(to_hex(n),64,'0'),decode('01','hex'),1,clock_timestamp()-interval '3 hours',clock_timestamp()-interval '1 hour',clock_timestamp()-interval '2 hours',1800,repeat('b',64) FROM generate_series(1,8) n"
                [||]

        let first =
            Housekeeping.prune
                database.OwnerSource
                database.Installation
                4
                (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                token

        let second =
            Housekeeping.prune
                database.OwnerSource
                database.Installation
                4
                (fun instant -> System.DateOnly.FromDateTime instant.UtcDateTime)
                token

        let renewal =
            sessions.Renew(live.Value, SessionResults.principal, [| 2uy |], (fun _ -> true), token)

        let logout = sessions.Remove(live.Value, SessionEndReason.Logout, token)
        let! batches = Task.WhenAll(first, second)
        do! Task.WhenAll(renewal :> Task, logout :> Task)

        Expect.equal
            (batches |> Array.sumBy _.SessionsDeleted)
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

let private recoverPausedOwner (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = bootstrap fixture

        let principal subject =
            PrincipalKey.human "https://issuer.example/" subject
            |> Result.defaultWith (fun issue -> failtestf "Synthetic identity: %A" issue)

        let prior = principal "owner-recovery-old"
        let replacement = principal "owner-recovery-new"
        let personId = CaseFixtures.actorId (id fixture 401)

        let change principal number taskName values =
            task {
                let! submission =
                    personSubmission
                        fixture.Store
                        (id fixture number)
                        principal
                        None
                        taskName
                        values

                return! Service.executeSubmission owner submission token
            }

        let! registered =
            change prior 401 "people.register" (Submissions.profile DomainInputs.actorProfile)

        acceptedPeople registered

        let! granted =
            grant
                owner
                (id fixture 402)
                prior
                {
                    GrantInput.Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! disabled = change prior 403 "people.disable" Map.empty
        acceptedPeople disabled

        let! paused =
            changed
                database
                (MaintenanceChange.Acquire(0L, Guid.Parse "10000000-0000-4000-8000-000000000097"))

        Expect.isTrue paused.State.Active "Synthetic installation is paused"

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        let recover principal actor =
            administration.RecoverOwner(
                {
                    Principal = principal
                    ActorId = actor
                    Name = None
                },
                token
            )

        let! recovered = recover prior None

        match recovered with
        | OwnerRecoveryResult.Recovered(id, _) ->
            Expect.equal id personId "Recovery finds the existing identity"
        | _ -> failtest "Expected a recorded restoration"

        let! roles = inspectPeople fixture prior token

        match roles with
        | Ok view ->
            Expect.equal
                (Set.ofList view.Installation)
                (set [ Role.Owner; Role.CaseReader ])
                "Owner and earlier role both remain"

            Expect.isTrue view.Actor.Value.Enabled "Recovery re-enables the existing account"
        | other -> failtestf "Recovery state was unreadable: %A" other

        let! rebound = recover replacement (Some personId)

        let recordedOperation =
            match rebound with
            | OwnerRecoveryResult.Recovered(id, operation) ->
                Expect.equal id personId "Explicit actor selection keeps identity and history"
                operation
            | _ -> failtest "Expected identity reassignment"

        let! reply =
            Service.operationGet
                (core fixture replacement)
                {
                    OperationId = OperationId.value recordedOperation
                }
                token

        match reply.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, false)) ->
            Expect.equal
                receipt.RecordedBy
                AuthorityRef.Operator
                "The recovered owner can see operator attribution"
        | other -> failtestf "Missing recovery receipt: %A" other

        let! before = administration.Head token
        let! repeated = recover replacement (Some personId)
        let! after = administration.Head token
        Expect.equal repeated (OwnerRecoveryResult.Unchanged personId) "Repeating returns unchanged"
        Expect.equal before after "No operation is appended for unchanged recovery"
        let! _ = verified database
        ()
    }

[<Tests>]
let tests =
    testList
        "owner operating lifecycle"
        [
            testCaseAsync
                "operator recovers an existing disabled person during maintenance and can rebind their sign-in"
                (run recoverPausedOwner)
            testCaseAsync
                "maintenance entry waits for a prior accepted transaction"
                (run entryWaitsForAcceptance)
            testCaseAsync
                "concurrent pruning renewal and logout retain their action-state rules"
                (run pruneConcurrency)
            testCaseAsync
                "status and maintenance on/off are persistent and read-scoped"
                (run statusAndModes)
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
