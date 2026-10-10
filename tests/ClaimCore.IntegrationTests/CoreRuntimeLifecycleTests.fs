module internal ClaimCore.IntegrationTests.CoreRuntimeLifecycleTests

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

type private DisposeCounter() =
    let mutable value = 0
    member _.Increment() = Interlocked.Increment(&value) |> ignore
    member _.Value = Volatile.Read(&value)

let private countedSource (disposeCount: DisposeCounter) =
    { new IDisposable with
        member _.Dispose() = disposeCount.Increment()
    }

let private disposalClosesAdmission () =
    let disposeCount = DisposeCounter()

    let admission =
        new RuntimeAdmission(
            countedSource disposeCount,
            TimeSpan.FromSeconds 2.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> Task.FromResult(countedSource (DisposeCounter()))),
            RuntimeAdmissionFixture.gate (fun () -> ())
        )

    use held = admission.Admit()
    let disposing = Task.Run(fun () -> (admission :> IDisposable).Dispose())

    Expect.isTrue
        (SpinWait.SpinUntil(
            (fun () ->
                try
                    use _unexpected = admission.Admit()
                    false
                with :? ObjectDisposedException ->
                    true),
            2000
        ))
        "Disposal must close admission"

    Expect.equal disposeCount.Value 0 "Admitted work still owns the source"
    Expect.isFalse disposing.IsCompleted "Disposal waits for the held operation"
    held.Dispose()
    Expect.isTrue (disposing.Wait(2000)) "Disposal finishes after the lease"
    Expect.equal disposeCount.Value 1 "One owner disposes the source exactly once"

let private boundedDisposalDefersCleanup () =
    let disposeCount = DisposeCounter()

    let admission =
        new RuntimeAdmission(
            countedSource disposeCount,
            TimeSpan.FromMilliseconds 50.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> Task.FromResult(countedSource (DisposeCounter()))),
            RuntimeAdmissionFixture.gate (fun () -> ())
        )

    use held = admission.Admit()
    let timer = Stopwatch.StartNew()
    (admission :> IDisposable).Dispose()
    timer.Stop()
    Expect.isTrue (timer.Elapsed < TimeSpan.FromSeconds 2.) "Drain is bounded"
    Expect.equal disposeCount.Value 0 "Timed-out disposal cannot close an admitted source"
    held.Dispose()

    Expect.isTrue
        (SpinWait.SpinUntil((fun () -> disposeCount.Value = 1), 2000))
        "Final lease performs deferred cleanup"

let private disposedRuntimeRefusesRetainedFacades () =
    let runtime =
        witnessedOpen (appConnection ()) CancellationToken.None |> await |> accepted

    let core = runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())
    let recovery = core.Recovery
    let lifecycle = core.Lifecycle
    (runtime :> IDisposable).Dispose()

    Expect.throwsT<ObjectDisposedException>
        (fun () -> core.Definition(CancellationToken.None) |> await |> ignore)
        "Definition refuses a disposed runtime"

    Expect.throwsT<ObjectDisposedException>
        (fun () -> core.Get("NO-SUCH-SYNTHETIC-CASE", CancellationToken.None) |> await |> ignore)
        "Queries refuse a disposed runtime"

    Expect.throwsT<ObjectDisposedException>
        (fun () ->
            recovery.List(RecoveryListView.Pending, None, 1, CancellationToken.None)
            |> await
            |> ignore)
        "Retained recovery facade also refuses admission"

    Expect.throwsT<ObjectDisposedException>
        (fun () ->
            lifecycle.Review("NO-SUCH-SYNTHETIC-CASE", CancellationToken.None)
            |> await
            |> ignore)
        "Retained lifecycle facade also refuses admission"

let private cancelledOpeningReturnsTypedFault () =
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()

    match witnessedOpen (appConnection ()) cancellation.Token |> await with
    | Error RuntimeOpenFault.RuntimeCancelled -> ()
    | _ -> failtest "Pre-cancelled opening must return RuntimeCancelled"

let private staleHealthStopsNewMutation () =
    let mutable dispatched = false

    use admission =
        new RuntimeAdmission(
            countedSource (DisposeCounter()),
            TimeSpan.FromSeconds 1.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> Task.FromResult(countedSource (DisposeCounter()))),
            RuntimeAdmissionFixture.gate (fun () -> invalidOp "Backup health needs owner renewal.")
        )

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            admission.Run(fun () ->
                dispatched <- true
                Task.FromResult 1)
            |> await
            |> ignore)
        "Stale backup health refuses before any mutation dispatch"

    Expect.isFalse dispatched "No mutating store call escaped a failed health gate"

    Expect.equal
        (admission.RunRead(fun () -> Task.FromResult 7) |> await)
        7
        "Read-only case work remains separately fenced"

    Expect.equal
        (admission.RunAuthoritySetup(fun () -> Task.FromResult 9) |> await)
        9
        "Typed authority repair remains possible while backup health is stale"

let private bootstrapPermitsAuthorityOnly () =
    let denied () =
        invalidOp "Real-data bootstrap has no case-work authority."

    use admission =
        new RuntimeAdmission(
            countedSource (DisposeCounter()),
            TimeSpan.FromSeconds 1.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> Task.FromResult(countedSource (DisposeCounter()))),
            { RuntimeAdmissionFixture.gate (fun () -> ()) with
                RequireCaseMutation = (fun _ -> task { denied () })
                RequireCaseRead = (fun _ -> task { denied () })
                RequireAuthoritySetup = (fun _ -> Task.FromResult(()))
                RequireAuthorityRead = (fun _ -> Task.FromResult(()))
                CommitHealthRequired = false
            }
        )

    Expect.throwsT<InvalidOperationException>
        (fun () -> admission.Run(fun () -> Task.FromResult 1) |> await |> ignore)
        "Bootstrap refuses case mutation"

    Expect.throwsT<InvalidOperationException>
        (fun () -> admission.RunRead(fun () -> Task.FromResult 1) |> await |> ignore)
        "Bootstrap refuses claimant-bearing read"

    Expect.equal
        (admission.RunAuthoritySetup(fun () -> Task.FromResult 2) |> await)
        2
        "Bootstrap permits only witnessed authority setup"

    Expect.equal
        (admission.RunAuthorityRead(fun () -> Task.FromResult 3) |> await)
        3
        "Bootstrap permits non-claimant authority readback"

let private endedCommitHealthScopeRefusesLateChild () =
    let mutable checks = 0

    let guard =
        { new IMutationCommitHealth with
            member _.VerifyLocked(_, _, _) =
                checks <- checks + 1
                Task.CompletedTask
        }

    let connection = Unchecked.defaultof<NpgsqlConnection>
    let transaction = Unchecked.defaultof<NpgsqlTransaction>

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let scope = MutationCommitHealth.enter guard

    (MutationCommitHealth.verifyLocked connection transaction CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    let late =
        Task.Run(fun () ->
            release.Task.GetAwaiter().GetResult()

            (MutationCommitHealth.verifyLocked connection transaction CancellationToken.None)
                .GetAwaiter()
                .GetResult())

    scope.Dispose()
    release.SetResult()

    Expect.throwsT<InvalidOperationException>
        (fun () -> late.GetAwaiter().GetResult())
        "A child retaining an ended execution context cannot pass commit health"

    Expect.equal checks 1 "Only the live scoped call reached the guard"

let private unguardedRealDataOutcomeRefuses () =
    use admission =
        new RuntimeAdmission(
            countedSource (DisposeCounter()),
            TimeSpan.FromSeconds 1.,
            (fun _ -> Task.FromResult(())),
            (fun _ -> Task.FromResult(countedSource (DisposeCounter()))),
            { RuntimeAdmissionFixture.gate (fun () -> ()) with
                CommitHealthRequired = true
            }
        )

    Expect.throwsT<InvalidOperationException>
        (fun () -> admission.Run(fun () -> Task.FromResult "accepted") |> await |> ignore)
        "An actor path without authority-tip commit health cannot return an accepted outcome"

    Expect.equal
        (admission.RunClassified(
            (fun () ->
                task {
                    do! Task.Delay(1)
                    return "invalid-input"
                }),
            (fun outcome -> outcome = "invalid-input")
         )
         |> await)
        "invalid-input"
        "A code-classified pre-admission refusal retains its typed result"

    MutationCommitHealth.verifyLocked
        (Unchecked.defaultof<NpgsqlConnection>)
        (Unchecked.defaultof<NpgsqlTransaction>)
        CancellationToken.None
    |> fun work -> work.GetAwaiter().GetResult()

let runtimeAdmissionTests =
    testList
        "runtime lifecycle"
        [
            testCase
                "[CC-RUN-001] disposal closes admission and drains a held operation"
                disposalClosesAdmission
            testCase
                "[CC-RUN-001] bounded disposal defers source cleanup until the final lease"
                boundedDisposalDefersCleanup
            testCase
                "[CC-RUN-001] disposed runtime refuses retained normal and recovery facades"
                disposedRuntimeRefusesRetainedFacades
            testCase
                "[CC-RUN-001] cancelled opening returns a safe typed fault"
                cancelledOpeningReturnsTypedFault
            testCase
                "[CC-BACKUP-001] stale backup health stops new mutation but not fenced reads"
                staleHealthStopsNewMutation
            testCase
                "[CC-BACKUP-001] real-data bootstrap admits authority setup but no case work"
                bootstrapPermitsAuthorityOnly
            testCase
                "[CC-BACKUP-001] ended commit-health scope refuses a late child mutation"
                endedCommitHealthScopeRefusesLateChild
            testCase
                "[CC-BACKUP-001] real-data outcome without commit-side health is withheld"
                unguardedRealDataOutcomeRefuses
        ]
