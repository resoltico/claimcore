module ClaimCore.IntegrationTests.AuthorityOperationFenceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FreshBaselineSupport

open ClaimCore.IntegrationTests.AuthorityOperationFenceFixture

let private openSmallRuntime () =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let principal = human "small-pool-owner"
        provision owner witness principal |> applied
        use source = RuntimeDataSource.create app
        let grants = new ActorGrantStore(source)
        let registry = new ActorGrantRegistry(source, witness)

        registry.SetGrant(
            principal,
            actorId grants principal,
            {
                Role = Role.CaseEditor
                Scope = GrantScope.Installation
            },
            true
        )
        |> completed
        |> applied

        use runtime =
            Runtime.OpenPostgres(
                pooled app 2,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                cancellation
            )
            |> completed
            |> accepted

        let request = newRequest ()

        match runtime.ForActor(principal).Execute(request, cancellation) |> completed with
        | SubmissionOutcome.Completed(_,
                                      _,
                                      DefiniteExecution.Accepted _,
                                      SettlementConfirmation.Confirmed) -> ()
        | _ -> failtest "A two-slot runtime admits and settles ordinary case work.")

let private saturatedOrdinaryPool () =
    withAuthorityRuntimeDatabase (fun owner app _ witness ->
        use runtime = resources app witness
        use first = runtime.DataSource.OpenConnection()
        use second = runtime.DataSource.OpenConnection()
        use transaction = first.BeginTransaction()

        use lockTip =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                first,
                transaction
            )

        lockTip.ExecuteScalar() |> ignore
        use timeout = new CancellationTokenSource(bound)
        let audit = RuntimeFullAudit.run runtime timeout.Token

        try
            waitForDatabaseLock owner "transactionid"
            Expect.isFalse audit.IsCompleted "Audit waits for the queued authority transaction."
            transaction.Rollback()
            let summary = completed audit

            Expect.equal
                summary.WitnessCutoff
                ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
                "Audit completes at its fixed tip."

            Expect.equal
                first.State
                System.Data.ConnectionState.Open
                "First ordinary slot stays occupied."

            Expect.equal
                second.State
                System.Data.ConnectionState.Open
                "Second ordinary slot stays occupied."
        finally
            timeout.Cancel())

let private cancelledExclusive () =
    withAuthorityRuntimeDatabase (fun owner app _ _ ->
        use blocker = new NpgsqlConnection(owner)
        blocker.Open()

        use shared =
            AuthorityOperationFence.acquireShared None blocker cancellation |> completed

        use source = RuntimeDataSource.create (pooled app 1)
        use queued = source.OpenConnection()
        let previousPid = queued.ProcessID
        use timeout = new CancellationTokenSource(bound)

        let pending =
            AuthorityOperationFence.acquireExclusive (Some source) queued timeout.Token

        waitForDatabaseLock owner "advisory"
        timeout.Cancel()

        Expect.throwsT<OperationCanceledException>
            (fun () -> completed pending |> ignore)
            "Queued exclusive acquisition cancels."

        Expect.equal
            queued.State
            System.Data.ConnectionState.Closed
            "Ambiguous cancelled connector is retired."

        shared.Dispose()
        use replacement = source.OpenConnection()

        Expect.notEqual
            replacement.ProcessID
            previousPid
            "Cancelled physical connector cannot re-enter the pool."

        use next =
            AuthorityOperationFence.acquireShared (Some source) replacement cancellation
            |> completed

        ())

let private cancelledAudit queued =
    withAuthorityRuntimeDatabase (fun owner app _ witness ->
        use runtime = resources app witness
        use blocker = new NpgsqlConnection(owner)
        blocker.Open()

        let shared =
            if queued then
                Some(AuthorityOperationFence.acquireShared None blocker cancellation |> completed)
            else
                None

        let fenced = signal ()
        use timeout = new CancellationTokenSource(bound)

        let audit =
            RuntimeFullAudit.runWith
                runtime
                (fun () -> Task.CompletedTask)
                (fun () ->
                    fenced.TrySetResult() |> ignore
                    Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token))
                timeout.Token

        try
            if queued then
                waitForDatabaseLock owner "advisory"
            else
                completed fenced.Task

            timeout.Cancel()

            let cancelled =
                try
                    completed audit |> ignore
                    false
                with :? OperationCanceledException ->
                    true

            Expect.isTrue cancelled "Audit cancellation propagates."
        finally
            timeout.Cancel()
            shared |> Option.iter (fun lease -> lease.Dispose())

        use next =
            AuthorityOperationFence.acquireShared None blocker cancellation |> completed

        next.Dispose()
        RuntimeFullAudit.run runtime cancellation |> completed |> ignore)

let private witnessAdvancesBeforeFence () =
    withAuthorityRuntimeDatabase (fun _ app _ witness ->
        use runtime = resources app witness

        let before =
            (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

        let intent = Guid.NewGuid()

        let append () =
            (witness
                .BeginAuthority(intent, [| 0x43uy; 0x43uy |], None, CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            |> ignore

            Task.CompletedTask

        let summary =
            RuntimeFullAudit.runWith runtime append (fun () -> Task.CompletedTask) cancellation
            |> completed

        Expect.equal
            summary.WitnessCutoff
            (before + 1L)
            "Cutoff captures the tip after witness fencing."

        Expect.equal
            summary.PendingIntents
            1L
            "The exact new orphan intent remains pending evidence."

        Expect.equal
            summary.WitnessCutoff
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
            "Audit includes the newly fenced witness tip.")

let tests =
    testList
        "authority operation fence"
        [
            testCase "[CC-AUDIT-001] two-slot runtime opens and settles case work" (fun _ ->
                openSmallRuntime ())
            testCase
                "[CC-AUDIT-001] full audit drains a queued transaction with both ordinary pool slots occupied"
                (fun _ -> saturatedOrdinaryPool ())
            testCase
                "[CC-AUDIT-001] full audit waits for actor authority settlement after primary commit"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        settlementFence owner app writer witness false))
            testCase
                "[CC-AUDIT-001] full audit waits for owner provisioning settlement after primary commit"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        settlementFence owner app writer witness true))
            testCase
                "[CC-AUDIT-001] cancelled queued exclusive acquisition retires its connector and releases authority"
                (fun _ -> cancelledExclusive ())
            testCase
                "[CC-AUDIT-001] cancelling an audit queued behind authority does not strand its session lease"
                (fun _ -> cancelledAudit true)
            testCase
                "[CC-AUDIT-001] cancelling an audit after its fence releases all authority locks"
                (fun _ -> cancelledAudit false)
            testCase
                "[CC-AUDIT-001] witness advancement before read fencing is included in the audit cutoff"
                (fun _ -> witnessAdvancesBeforeFence ())
            testCase
                "[CC-AUDIT-001] full audit revalidates a same-name weaker check after runtime resources open"
                (fun _ -> weakerCatalogAfterOpening ())
            testCase
                "[CC-BACKUP-001] backup capture drains actor settlement and retains the complete authority fence"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        captureSettlementFence owner app writer witness))
        ]
