module ClaimCore.IntegrationTests.WitnessedCapacityTests

open System
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FreshBaselineSupport
open ClaimCore.IntegrationTests.AuthorityOperationFenceFixture
open ClaimCore.IntegrationTests.WitnessedCapacityAuditFixture

let private acceptedExecution (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic command must settle before capacity measurements."

let private populate (core: IActorClaimsCore) =
    let prefix = "WIT-CAP-" + Guid.NewGuid().ToString("N")

    for n in 1..55 do
        acceptedExecution core (openRequest (Guid.NewGuid()) (prefix + sprintf "-%03d" n))

    let reference = prefix + "-001"

    for version in 1L .. 60L do
        let command = if version % 2L = 1L then Command.Close else Command.Reopen

        acceptedExecution
            core
            {
                OperationId = Guid.NewGuid()
                CaseReference = reference
                ExpectedVersion = version
                Command = command
            }

    reference

let private verifyPages (core: IActorClaimsCore) reference =
    let first =
        core.List({ AfterCursor = None; Limit = 50 }, CancellationToken.None) |> await

    match first with
    | QueryOutcome.Succeeded value ->
        Expect.equal value.Items.Length 50 "First case window is bounded."

        match value.NextCursor with
        | None -> failtest "Case continuation must exist."
        | Some cursor ->
            match
                core.List(
                    {
                        AfterCursor = Some cursor
                        Limit = 50
                    },
                    CancellationToken.None
                )
                |> await
            with
            | QueryOutcome.Succeeded tail ->
                Expect.equal tail.Items.Length 5 "All remaining cases are reached once."
            | _ -> failtest "Expected a second case page."
    | _ -> failtest "Expected an authorized first case page."

    let history cursor =
        core.History(
            {
                CaseReference = reference
                AfterCursor = cursor
                Limit = 50
                Detail = HistoryDetail.Full
            },
            CancellationToken.None
        )
        |> await

    match history None with
    | QueryOutcome.Succeeded(Lookup.Found page) ->
        Expect.equal page.Entries.Length 50 "History is one bounded window."

        match history page.NextCursor with
        | QueryOutcome.Succeeded(Lookup.Found tail) ->
            Expect.equal tail.Entries.Length 11 "History continuation has no duplicates."
        | _ -> failtest "Expected a second witnessed history page."
    | _ -> failtest "Expected witnessed accepted history."

let private exportPreparation (core: IActorClaimsCore) reference =
    let request =
        {
            OperationId = Guid.NewGuid()
            CaseReference = reference
            ExpectedVersion = 61L
            Command = Command.Close
        }

    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Expected exact digest.")

        match
            core.Recovery.ExportEnvelope(request.OperationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
        | _ -> failtest "Expected a witnessed export."
    | _ -> failtest "Expected retained preparation."

let private competingAuditAndActorWork () =
    withRuntime (fun owner core auditResources ->
        let reference = populate core
        verifyPages core reference
        exportPreparation core reference

        let summary = runVolumeAudit auditResources

        Expect.equal summary.Cases 55L "Full audit crosses its case page boundary."

        Expect.equal
            summary.AcceptedOperations
            115L
            "Full replay crosses multiple history windows."

        holdAudit owner core auditResources reference (resumeAfterCancellation auditResources))

let private assertionFailureJoinsQueuedWork () =
    withRuntime (fun owner core auditResources ->
        let reference = "WIT-CAP-CLEANUP-" + Guid.NewGuid().ToString("N")
        acceptedExecution core (openRequest (Guid.NewGuid()) reference)
        let mutable queued = None

        let injectFailure () =
            holdAudit owner core auditResources reference (fun (_, audit, mutation, read) ->
                queued <- Some(audit, mutation, read)
                invalidOp "Synthetic assertion after actor work queues.")

        let failure =
            try
                injectFailure ()
                None
            with error ->
                Some error

        match failure with
        | Some(:? InvalidOperationException as error) ->
            Expect.equal
                error.Message
                "Synthetic assertion after actor work queues."
                "Cleanup preserves the original assertion failure."
        | _ -> failtest "Expected the exact injected assertion failure."

        let audit, mutation, read =
            queued |> Option.defaultWith (fun () -> failtest "Expected all queued tasks.")

        Expect.isTrue audit.IsCompleted "Held audit has ended before fixture dependencies unwind."

        Expect.isTrue
            mutation.IsCompleted
            "Admitted mutation has settled before returning failure."

        Expect.isTrue read.IsCompleted "Queued disclosure has settled before returning failure."

        match mutation |> await with
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
        | _ -> failtest "Cleanup must preserve the admitted mutation's acceptance."

        match read |> await with
        | QueryOutcome.Succeeded(Lookup.Found _) -> ()
        | _ -> failtest "Cleanup must preserve the queued read's result."

        let summary = runVolumeAudit auditResources
        Expect.equal summary.Cases 2L "Subsequent audit includes the settled mutation.")

let private workloadDeadlineJoinsBlockedAudit () =
    withRuntime (fun owner _ auditResources ->
        use barrier = new NpgsqlConnection(owner)
        barrier.Open()

        use lease =
            AuthorityOperationFence.acquireShared None barrier CancellationToken.None
            |> await

        let pending = startVolumeAuditWithin (TimeSpan.FromSeconds 5.) auditResources

        try
            waitForDatabaseLock owner "advisory"

            Expect.throwsT<OperationCanceledException>
                (fun () -> pending |> await |> ignore)
                "The workload deadline cancels and joins the audit while authority remains held."

            Expect.isTrue pending.IsCanceled "No audit task survives its deadline refusal."

            Expect.equal
                (scalar
                    owner
                    ("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() "
                     + "AND wait_event_type='Lock' AND wait_event='advisory'")
                :?> int64)
                0L
                "The cancelled connector is retired before authority is released."
        finally
            lease.Dispose()

            try
                pending |> await |> ignore
            with :? OperationCanceledException ->
                ()

        let summary = runVolumeAudit auditResources

        Expect.equal
            summary.Cases
            0L
            "The next full audit can acquire authority after cancellation.")

let tests =
    testList
        "witnessed capacity and competing work"
        [
            testCase
                "[CC-AUDIT-001] paged witnessed volume export and actor work survive audit contention with small pools"
                competingAuditAndActorWork
            testCase
                "[CC-AUDIT-001] failed contention assertions join the audit and admitted actor work"
                assertionFailureJoinsQueuedWork
            testCase
                "[CC-AUDIT-001] workload deadlines join blocked full audits before releasing fixture authority"
                workloadDeadlineJoinsBlockedAudit
        ]
