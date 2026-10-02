module ClaimCore.IntegrationTests.RuntimeSchedulingTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting
open ClaimCore.Postgres

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private longAuditLeavesAnInterval () =
    let entered = signal ()
    let release = signal ()
    let second = signal ()
    let interval = TimeSpan.FromMilliseconds 250.
    let mutable count = 0
    let mutable completedAt = 0L
    let mutable elapsed = TimeSpan.Zero

    let audit _ =
        task {
            if Interlocked.Increment(&count) = 1 then
                entered.SetResult()
                do! release.Task
                completedAt <- Stopwatch.GetTimestamp()
            else
                elapsed <- Stopwatch.GetElapsedTime(completedAt)
                second.TrySetResult() |> ignore
        }
        :> Task

    use cadence = new RuntimeAuditCadence(audit, interval)
    Expect.isTrue (entered.Task.Wait(2000)) "First audit starts."
    Task.Delay(interval + interval + interval).GetAwaiter().GetResult()
    release.SetResult()
    Expect.isTrue (second.Task.Wait(2000)) "A subsequent audit remains scheduled."

    Expect.isGreaterThan
        elapsed
        (TimeSpan.FromMilliseconds 200.)
        "No accumulated tick starts an immediate audit."

    cadence.RequireHealthy()

let private stopDoesNotWaitForCallbacks () =
    let entered = signal ()
    use callbackEntered = new ManualResetEventSlim()
    use releaseCallback = new ManualResetEventSlim()

    let audit (token: CancellationToken) =
        task {
            use _callback =
                token.Register(fun () ->
                    callbackEntered.Set()
                    releaseCallback.Wait())

            entered.SetResult()
            do! Task.Delay(Timeout.InfiniteTimeSpan, token)
        }
        :> Task

    let cadence = new RuntimeAuditCadence(audit, TimeSpan.FromMilliseconds 20.)

    try
        Expect.isTrue (entered.Task.Wait(2000)) "Audit registers its cancellation callback."
        let stop = Task.Run(cadence.RequestStop)
        Expect.isTrue (stop.Wait(2000)) "Stop publishes cancellation without joining callbacks."
        Expect.isTrue (callbackEntered.Wait(2000)) "The callback is active."
        let dispose = Task.Run(fun () -> (cadence :> IDisposable).Dispose())
        Expect.isFalse dispose.IsCompleted "Cleanup owns the incomplete callback."
        releaseCallback.Set()
        Expect.isTrue (dispose.Wait(2000)) "Cleanup joins worker and callback."
        cadence.RequestStop()
    finally
        releaseCallback.Set()
        (cadence :> IDisposable).Dispose()

let private emptyLease () =
    { new IDisposable with
        member _.Dispose() = ()
    }

let private useGate: RuntimeUseGate =
    {
        RequireCaseRead = ignore
        RequireCaseMutation = ignore
        RequireAuthorityRead = ignore
        RequireAuthoritySetup = ignore
        CommitHealthRequired = false
        CommitHealth =
            { new ICaseMutationCommitHealth with
                member _.VerifyLocked(_, _) = ()
            }
    }

let private stopBeforeActorDrain () =
    let auditEntered = signal ()
    let auditStopped = signal ()

    let actorRelease =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closed = 0

    let audit (token: CancellationToken) =
        task {
            use _callback = token.Register(fun () -> auditStopped.TrySetResult() |> ignore)
            auditEntered.TrySetResult() |> ignore
            do! Task.Delay(Timeout.InfiniteTimeSpan, token)
        }
        :> Task

    let cadence = new RuntimeAuditCadence(audit, TimeSpan.FromMilliseconds 20.)

    let resources =
        { new IDisposable with
            member _.Dispose() =
                (cadence :> IDisposable).Dispose()
                Interlocked.Increment(&closed) |> ignore
        }

    use admission =
        new RuntimeAdmission(resources, TimeSpan.FromSeconds 2., ignore, emptyLease, useGate)

    let pending = admission.RunRead(fun () -> actorRelease.Task)

    try
        Expect.isTrue
            (auditEntered.Task.Wait(2000))
            "Audit is active while actor work owns a lease."

        let dispose =
            Task.Run(fun () ->
                admission.CloseAndDrain(fun () ->
                    Expect.throwsT<ObjectDisposedException>
                        (fun () -> admission.Admit() |> ignore)
                        "Admission closes before stop."

                    cadence.RequestStop()))

        Expect.isTrue (auditStopped.Task.Wait(2000)) "Audit cancellation precedes actor completion."
        Expect.isFalse pending.IsCompleted "The admitted actor was not cancelled."
        actorRelease.SetResult(42)
        Expect.equal (pending.GetAwaiter().GetResult()) 42 "The actor result is unchanged."
        Expect.isTrue (dispose.Wait(2000)) "Cleanup finishes after both owners drain."
        Expect.equal closed 1 "Resource cleanup occurs once."
    finally
        actorRelease.TrySetResult(42) |> ignore
        (cadence :> IDisposable).Dispose()

let tests =
    testList
        "runtime scheduling and shutdown"
        [
            testCase
                "[CC-AUDIT-001] long complete audits leave a completion-based interval"
                longAuditLeavesAnInterval
            testCase
                "[CC-RUN-001] audit stop is nonblocking and cleanup joins cancellation callbacks"
                stopDoesNotWaitForCallbacks
            testCase
                "[CC-RUN-001] shutdown stops audit before draining admitted actor work"
                stopBeforeActorDrain
        ]
