module ClaimCore.IntegrationTests.RuntimeResourceCleanupTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting

let private admission source timeout =
    new RuntimeAdmission(
        source,
        timeout,
        (fun _ -> Task.FromResult(())),
        (fun _ ->
            task {
                return
                    ((fun () ->
                        { new IDisposable with
                            member _.Dispose() = ()
                        })) ()
            }),
        {
            RequireCaseMutation = (fun _ -> Task.FromResult(()))
            RequireCaseRead = (fun _ -> Task.FromResult(()))
            RequireAuthoritySetup = (fun _ -> Task.FromResult(()))
            RequireAuthorityRead = (fun _ -> Task.FromResult(()))
            RequireAuditTrust = (fun () -> ())
            AuthorityHealth =
                { new ClaimCore.Postgres.IMutationCommitHealth with
                    member _.VerifyLocked(_, _, _) = Task.CompletedTask
                }
            CommitHealth =
                { new ClaimCore.Postgres.IMutationCommitHealth with
                    member _.VerifyLocked(_, _, _) = Task.CompletedTask
                }
            CommitHealthRequired = false
        }
    )

let private attemptsEveryResource () =
    let attempted = ResizeArray<int>()

    let resource index throws =
        { new IDisposable with
            member _.Dispose() =
                attempted.Add(index)

                if throws then
                    invalidOp "Synthetic private provider canary"
        }

    let error =
        try
            RuntimeResourceCleanup.disposeAll
                [ resource 1 true; resource 2 false; resource 3 true; resource 4 false ]

            None
        with :? InvalidOperationException as failure ->
            Some failure

    Expect.equal (Seq.toList attempted) [ 1; 2; 3; 4 ] "Every owned resource was attempted in order"

    Expect.equal
        (error |> Option.map _.Message)
        (Some "ClaimCore runtime resource cleanup failed.")
        "Failure is bounded without provider details"

let private closingPrecedesCleanupWait () =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let finish =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let source =
        { new IDisposable with
            member _.Dispose() =
                entered.TrySetResult() |> ignore
                finish.Task.GetAwaiter().GetResult()
        }

    use admission = admission source (TimeSpan.FromMilliseconds 50.)

    try
        (admission :> IDisposable).Dispose()
        Expect.isTrue (entered.Task.Wait(2000)) "Cleanup can remain active after bounded disposal"

        Expect.throwsT<ObjectDisposedException>
            (fun () -> admission.Admit() |> ignore)
            "No new work enters while cleanup waits"
    finally
        finish.TrySetResult() |> ignore

let private delayedFailurePreservesOutcome () =
    let finish =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closed = 0

    let source =
        { new IDisposable with
            member _.Dispose() =
                closed <- closed + 1
                invalidOp "PRIVATE-PROVIDER-CANARY"
        }

    let admission = admission source (TimeSpan.FromMilliseconds 50.)
    let pending = admission.RunRead(fun () -> finish.Task)
    (admission :> IDisposable).Dispose()
    finish.SetResult(42)

    Expect.equal
        (pending.GetAwaiter().GetResult())
        42
        "Late cleanup cannot replace the admitted result"

    let completion = admission.CleanupCompletion

    Expect.isFalse
        (completion.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult())
        "Failure knowledge remains available"

    Expect.equal
        completion.Status
        TaskStatus.RanToCompletion
        "No unobserved faulted completion task"

    Expect.isNull completion.Exception "No provider exception is retained"

    let error =
        try
            (admission :> IDisposable).Dispose()
            failtest "A settled cleanup failure cannot pass another disposer."
        with :? InvalidOperationException as error ->
            error

    Expect.equal error.Message "ClaimCore runtime cleanup failed." "Safe repeat-disposal failure"
    Expect.isNull error.InnerException "No provider detail in disposal"
    Expect.equal closed 1 "Cleanup is never repeated"

let private asyncShutdownRetainsLease () =
    let mutable closed = false

    let source =
        { new IDisposable with
            member _.Dispose() = closed <- true
        }

    use current = admission source (TimeSpan.FromMilliseconds 10.)
    let lease = current.Admit()
    let shutdown = current.CloseAndDrainAsync(fun () -> ())
    Expect.isFalse shutdown.IsCompleted "Shutdown awaits the admitted lease"
    Expect.isFalse closed "Resources remain owned"

    Expect.throwsT<ObjectDisposedException>
        (fun () -> current.Admit() |> ignore)
        "New work is closed"

    lease.Dispose()
    shutdown.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    Expect.isTrue closed "Shutdown completion proves resource release"

let tests =
    testList
        "runtime cleanup ownership"
        [
            testCase
                "[CC-RUN-001] asynchronous shutdown waits for admitted leases and cleanup"
                asyncShutdownRetainsLease
            testCase
                "[CC-RUN-001] deferred cleanup failure preserves admitted outcomes and safe completion"
                delayedFailurePreservesOutcome
            testCase
                "[CC-RUN-001] runtime cleanup attempts every resource despite disposal faults"
                attemptsEveryResource
            testCase
                "[CC-RUN-001] runtime closes admission before cleanup completion"
                closingPrecedesCleanupWait
        ]
