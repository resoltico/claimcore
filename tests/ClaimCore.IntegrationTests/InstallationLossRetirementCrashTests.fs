module ClaimCore.IntegrationTests.InstallationLossRetirementCrashTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementCrashFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementCommitFault

let private checkPending (context: Context) =
    let tip = context.Witness.Snapshot()
    Expect.isTrue tip.LossRetirementPending "W0 still fences the old installation."
    Expect.isFalse tip.LossRetired "Missing W1 is not a settled retirement."
    use audit = new NpgsqlConnection(context.OwnerConnectionString)
    audit.Open()
    let summary = DataAudit.run audit context.Witness CancellationToken.None |> await
    Expect.equal summary.PendingIntents 1L "Full audit retains one pending W0."

let private checkReconciled (context: Context) (connection: NpgsqlConnection) (decision: Decision) =
    match reconcile context connection decision with
    | InstallationLossRetirementOutcome.Retired(id, sequence, _) ->
        Expect.equal id decision.Value.RetirementId "Original signed identity survives."
        Expect.equal sequence (decision.BeforeSequence + 2L) "One W0 and one W1 are retained."
    | _ -> failtest "Exact loss retirement reconciliation failed."

let private heldOwnerAudit
    (context: Context)
    (entered: ManualResetEventSlim)
    (release: ManualResetEventSlim)
    =
    Task.Run(fun () ->
        let keyId = context.Witness.KeyCustody.ActiveKeyId
        let material = witnessKey ()
        use custody = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
        CryptographicOperations.ZeroMemory(material)
        use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

        let summary, _, inspected =
            DatabaseVerifyData.auditedWith
                context.OwnerConnectionString
                context.Writer
                custody
                suppression
                (fun _ _ _ summary _ ->
                    entered.Set()

                    if not (release.Wait(TimeSpan.FromSeconds 30.)) then
                        failtest "Synthetic owner audit was not released."

                    summary.PendingIntents)

        Expect.equal summary.PendingIntents 1L "Owner audit retains pending W0 knowledge."
        inspected)

let private settleBehindAuthorityLock
    (context: Context)
    (decision: Decision)
    intent
    (started: TaskCompletionSource<int>)
    =
    Task.Run(fun () ->
        use owner = new NpgsqlConnection(context.OwnerConnectionString)
        owner.Open()
        use pidCommand = new NpgsqlCommand("SELECT pg_backend_pid()", owner)
        started.SetResult(pidCommand.ExecuteScalar() :?> int)

        let input: LossRetirementCommitInput =
            {
                Decision = decision.Value
                DecisionBytes = decision.Canonical
                OwnerSignatureOne = decision.FirstSignature
                OwnerSignatureTwo = decision.SecondSignature
                OperationIdentitySource = decision.KnownSource
                ReportEvidence = None
                CheckpointEvidence = None
            }

        InstallationLossRetirementCommit.finish
            owner
            (witnessOwnerFor context.Writer)
            context.Witness
            input
            intent
            decision.Commitments)

let private waitingForAuthority (observer: NpgsqlConnection) pid =
    use command =
        new NpgsqlCommand(
            "SELECT wait_event_type='Lock' FROM pg_stat_activity WHERE pid=@pid",
            observer
        )

    command.Parameters.AddWithValue("pid", pid) |> ignore

    match command.ExecuteScalar() with
    | :? bool as waiting -> waiting
    | _ -> false

let private auditSerializesW1 (context: Context) (decision: Decision) intent =
    use entered = new ManualResetEventSlim()
    use release = new ManualResetEventSlim()
    let audit = heldOwnerAudit context entered release

    if not (entered.Wait(TimeSpan.FromSeconds 10.)) then
        release.Set()
        audit.GetAwaiter().GetResult() |> ignore
        failtest "Owner audit did not acquire its primary barrier."

    let prior = context.Witness.Snapshot().TipSequence

    let started =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let settling = settleBehindAuthorityLock context decision intent started

    let blocked, unchanged =
        try
            let pid = started.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            use observer = new NpgsqlConnection(context.OwnerConnectionString)
            observer.Open()

            let waiting =
                SpinWait.SpinUntil((fun () -> waitingForAuthority observer pid), 10000)

            waiting, context.Witness.Snapshot().TipSequence = prior
        finally
            release.Set()

    Expect.equal (audit.GetAwaiter().GetResult()) 1L "Pending owner audit completes cleanly."

    match settling.GetAwaiter().GetResult() with
    | InstallationLossRetirementOutcome.Retired(id, sequence, _) ->
        Expect.equal id decision.Value.RetirementId "W1 keeps the original signed identity."
        Expect.equal sequence (decision.BeforeSequence + 2L) "W1 follows the original W0."
    | _ -> failtest "W1 did not settle after the owner audit released its barrier."

    Expect.isTrue blocked "W1 waits while owner audit holds primary authority."
    Expect.isTrue unchanged "Witness tip cannot move during the pending owner audit."

let private committedBeforeW1 (context: Context) =
    let decision = prepare context Array.empty InstallationLossOperationSet.Unknown
    let intent = w0 context decision
    commitPrimary context.Primary decision intent

    Expect.isTrue
        (InstallationLossRetirementPrimary.exact
            context.Primary
            decision.Value
            decision.Canonical
            decision.FirstSignature
            decision.SecondSignature
            intent
            decision.Commitments)
        "Primary receipt committed before synthetic response loss."

    checkPending context
    auditSerializesW1 context decision intent

let private interruptedCommit (context: Context) =
    let decision = prepare context Array.empty InstallationLossOperationSet.Unknown
    let intent = w0 context decision
    abortDuringCommit context decision intent
    use reopened = new NpgsqlConnection(context.OwnerConnectionString)
    reopened.Open()

    use absent =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.installation_loss_retirements", reopened)

    Expect.equal
        (absent.ExecuteScalar() :?> int64)
        0L
        "Terminated in-COMMIT primary transaction left no receipt."

    checkPending context
    checkReconciled context reopened decision

let private lostW1Response (context: Context) =
    let decision = prepare context Array.empty InstallationLossOperationSet.Unknown

    let completed =
        InstallationLossRetirementAdministration.record
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            decision.Canonical
            decision.FirstSignature
            decision.SecondSignature
            decision.KnownSource
            None
            None

    match completed with
    | InstallationLossRetirementOutcome.Retired _ -> ()
    | _ -> failtest "Synthetic W1 did not settle before lost output."

    let tip = context.Witness.Snapshot()

    try
        raise (IOException("synthetic lost W1 response"))
    with :? IOException ->
        ()

    checkReconciled context context.Primary decision

    Expect.equal
        (context.Witness.Snapshot().TipSequence)
        tip.TipSequence
        "Lost output retry appends no new witness authority."

let private run scenario =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        InstallationLossRetirementFixture.run owner app writer witness scenario)

let tests =
    testList
        "installation loss crash boundaries"
        [
            testCase
                "[CC-BACKUP-001] primary COMMIT response loss before W1 preserves exact pending receipt"
                (fun _ -> run committedBeforeW1)
            testCase
                "[CC-BACKUP-001] terminated in-COMMIT primary transaction keeps W0 pending"
                (fun _ -> run interruptedCommit)
            testCase
                "[CC-BACKUP-001] W1 response loss reconciles only the historical terminal ticket"
                (fun _ -> run lostW1Response)
        ]
