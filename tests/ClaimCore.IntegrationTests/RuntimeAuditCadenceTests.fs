module ClaimCore.IntegrationTests.RuntimeAuditCadenceTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private emptyLease () =
    { new IDisposable with
        member _.Dispose() = ()
    }

let private failedAuditQuarantinesActorLanes () =
    let first =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    use cadence =
        new RuntimeAuditCadence(
            (fun _ ->
                first.TrySetResult() |> ignore
                Task.FromException(InvalidOperationException("Synthetic audit failure"))),
            TimeSpan.FromMilliseconds 20.
        )

    Expect.isTrue (first.Task.Wait(2000)) "Scheduled audit runs without an actor request."
    cadence.Completion.GetAwaiter().GetResult()
    let denied () = cadence.RequireHealthy()
    let mutable dispatched = false

    use admission =
        new RuntimeAdmission(
            emptyLease (),
            TimeSpan.FromSeconds 1.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> task { return (emptyLease) () }),
            { RuntimeAdmissionFixture.gate (fun () -> ()) with
                RequireCaseMutation = (fun _ -> task { denied () })
                RequireCaseRead = (fun _ -> task { denied () })
                RequireAuthoritySetup = (fun _ -> task { denied () })
                RequireAuthorityRead = (fun _ -> task { denied () })
                CommitHealthRequired = false
            }
        )

    let observe () =
        dispatched <- true
        Task.FromResult 1

    Expect.throwsT<InvalidOperationException>
        (fun () -> admission.RunAuthoritySetup(observe) |> await |> ignore)
        "A failed audit refuses actor-bound authority setup."

    Expect.throwsT<InvalidOperationException>
        (fun () -> admission.RunAuthorityRead(observe) |> await |> ignore)
        "A failed audit refuses actor-bound authority observation."

    Expect.isFalse dispatched "No actor authority work escaped audit quarantine."

let private repeatedCadenceStaysHealthy () =
    let third =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable count = 0

    use cadence =
        new RuntimeAuditCadence(
            (fun _ ->
                if Interlocked.Increment(&count) = 3 then
                    third.TrySetResult() |> ignore

                Task.CompletedTask),
            TimeSpan.FromMilliseconds 20.
        )

    Expect.isTrue (third.Task.Wait(2000)) "Complete audit repeats at the configured cadence."
    cadence.RequireHealthy()
    Expect.isTrue (Volatile.Read(&count) >= 3) "Repeated audits completed."

let private overdueCadenceIsSticky () =
    let mutable ticks = 0L

    let clock =
        {
            Timestamp = (fun () -> Volatile.Read(&ticks))
            Elapsed = (fun since -> TimeSpan.FromTicks(Volatile.Read(&ticks) - since))
        }

    use cadence =
        new RuntimeAuditCadence((fun _ -> Task.CompletedTask), TimeSpan.FromHours 24., clock)

    Interlocked.Exchange(&ticks, (TimeSpan.FromHours 26.).Ticks) |> ignore
    cadence.RequireHealthy()

    Interlocked.Exchange(&ticks, (TimeSpan.FromHours 26. + TimeSpan.FromTicks 1L).Ticks)
    |> ignore

    Expect.throwsT<InvalidOperationException>
        (fun () -> cadence.RequireHealthy())
        "An overdue complete audit fails case-work admission."

    Interlocked.Exchange(&ticks, 0L) |> ignore

    Expect.throwsT<InvalidOperationException>
        (fun () -> cadence.RequireHealthy())
        "A clock correction cannot clear audit quarantine."

let private disposalCancelsActiveAudit () =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let cancelled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable count = 0

    let audit (token: CancellationToken) =
        task {
            Interlocked.Increment(&count) |> ignore
            entered.TrySetResult() |> ignore

            try
                do! Task.Delay(Timeout.InfiniteTimeSpan, token)
            with :? OperationCanceledException ->
                cancelled.TrySetResult() |> ignore
        }
        :> Task

    let cadence = new RuntimeAuditCadence(audit, TimeSpan.FromMilliseconds 20.)

    try
        Expect.isTrue (entered.Task.Wait(2000)) "One scheduled audit is active."
    finally
        (cadence :> IDisposable).Dispose()

    (cadence :> IDisposable).Dispose()

    Expect.isTrue cancelled.Task.IsCompleted "Disposal cancelled the active audit."
    Expect.isTrue cadence.Completion.IsCompletedSuccessfully "No audit worker survives disposal."
    Expect.equal (Volatile.Read(&count)) 1 "Disposal prevents another scheduled audit."

let private openRuntimeWithCadence app writer =
    let previous =
        Environment.GetEnvironmentVariable("CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS")

    Environment.SetEnvironmentVariable("CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS", "60")

    try
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
        |> accepted
    finally
        Environment.SetEnvironmentVariable("CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS", previous)

let private acceptedCase (core: IActorClaimsCore) =
    let request =
        openRequest (Guid.NewGuid()) ("CADENCE-" + Guid.NewGuid().ToString("N"))

    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> request.CaseReference
    | _ -> failtest "A synthetic case must be accepted before tampering."

let private tamper owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.cases SET claimant_name='Synthetic changed claimant' "
            + "WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    Expect.equal (command.ExecuteNonQuery()) 1 "One isolated projection changed."

let private awaitAuditQuarantine (core: IActorClaimsCore) =
    let elapsed = Stopwatch.StartNew()
    let mutable quarantined = false

    while not quarantined && elapsed.Elapsed < TimeSpan.FromSeconds 75. do
        try
            core.Definition(CancellationToken.None) |> await |> ignore
            Thread.Sleep 100
        with :? InvalidOperationException ->
            quarantined <- true

    Expect.isTrue quarantined "The scheduled whole-installation audit detected row tampering."

let private assertActorQuarantine
    (core: IActorClaimsCore)
    principal
    reference
    (witness: WitnessProtocol)
    =
    Expect.throwsT<InvalidOperationException>
        (fun () -> core.Get(reference, CancellationToken.None) |> await |> ignore)
        "Case reads close after audit failure."

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            core.Management.Observe(Guid.NewGuid(), CancellationToken.None)
            |> await
            |> ignore)
        "Authority observation closes after audit failure."

    let cutoff =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            core.Management.SetGrant(
                Guid.NewGuid(),
                principal,
                Role.CaseEditor,
                GrantTarget.Installation,
                false,
                CancellationToken.None
            )
            |> await
            |> ignore)
        "Actor authority mutation closes after audit failure."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        cutoff
        "No new authority ticket escaped."

let private scheduledAuditQuarantinesTamperedCase () =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let principal = human "cadence-owner"
        provision owner witness principal |> applied
        use source = RuntimeDataSource.create app
        let grants = source
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
        |> await
        |> applied

        use runtime = openRuntimeWithCadence app writer
        let core = runtime.ForActor principal
        core.Definition(CancellationToken.None) |> await |> ignore
        let reference = acceptedCase core
        tamper owner reference
        awaitAuditQuarantine core
        assertActorQuarantine core principal reference witness)

let tests =
    testList
        "runtime full-audit cadence"
        [
            testCase
                "[CC-AUDIT-001] scheduled full-audit failure closes actor authority lanes"
                failedAuditQuarantinesActorLanes
            testCase
                "[CC-AUDIT-001] scheduled complete audits repeat and remain healthy"
                repeatedCadenceStaysHealthy
            testCase
                "[CC-AUDIT-001] overdue complete audit closes admission persistently"
                overdueCadenceIsSticky
            testCase
                "[CC-AUDIT-001] runtime disposal cancels active scheduled audit"
                disposalCancelsActiveAudit
            testCase
                "[CC-AUDIT-001] scheduled audit quarantines post-opening row tamper"
                scheduledAuditQuarantinesTamperedCase
        ]
