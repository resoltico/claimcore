module ClaimCore.Tests.RuntimeDrainTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Server.Composition

[<Tests>]
let tests =
    testList
        "runtime teardown outcomes"
        [
            testCaseAsync
                "throwing close completes drain exactly once without escaping lease release"
                (async {
                    let mutable closes = 0

                    let gate =
                        RuntimeLeases(fun () ->
                            Interlocked.Increment(&closes) |> ignore
                            failwith "private provider detail")

                    let lease = gate.Acquire() |> Option.get
                    let stopping = gate.Stop()
                    Expect.isFalse stopping.IsCompleted "Admitted work retains provider"
                    lease.Dispose()
                    lease.Dispose()
                    let! outcome = stopping.WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask

                    Expect.equal
                        outcome
                        ShutdownOutcome.TeardownFailed
                        "Failure is a separate typed teardown result"

                    let! repeated = gate.Shutdown(TimeSpan.FromSeconds 1.) |> Async.AwaitTask

                    Expect.equal
                        repeated
                        ShutdownOutcome.TeardownFailed
                        "Later waits retain the failure"

                    Expect.equal closes 1 "No duplicate provider disposal"
                    Expect.isNone (gate.Acquire()) "Admission remains closed"
                })
            testCaseAsync
                "blocked provider cleanup respects shutdown bound and does not hold admission lock"
                (async {
                    let started =
                        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                    use release = new ManualResetEventSlim(false)

                    let gate =
                        RuntimeLeases(fun () ->
                            started.TrySetResult() |> ignore
                            release.Wait())

                    try
                        let stopping = gate.Shutdown(TimeSpan.FromMilliseconds 20.)
                        do! started.Task.WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask
                        Expect.isNone (gate.Acquire()) "New work is refused while close is blocked"

                        let! outcome =
                            stopping.WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask

                        Expect.equal
                            outcome
                            ShutdownOutcome.BoundExpired
                            "Bound includes provider teardown"

                        Expect.isFalse
                            (gate.Stop().IsCompleted)
                            "Provider is still closing, not falsely drained"
                    finally
                        release.Set()

                    let! final = gate.Stop().WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask
                    Expect.equal final ShutdownOutcome.Drained "Late teardown remains awaitable"
                })
            testCaseAsync
                "teardown failure cannot replace an admitted callback result or primary error"
                (async {
                    let gate = RuntimeLeases(fun () -> failwith "private cleanup detail")

                    let finish =
                        TaskCompletionSource<int>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    let running = ActorAccess.run gate id () (fun () -> finish.Task)
                    let stopping = gate.Stop()
                    finish.SetResult 42
                    let! result = running |> Async.AwaitTask
                    Expect.equal result (Some 42) "Callback result survives failed teardown"
                    let! outcome = stopping.WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask

                    Expect.equal
                        outcome
                        ShutdownOutcome.TeardownFailed
                        "Failure remains independently visible"

                    let gate = RuntimeLeases(fun () -> failwith "secondary cleanup detail")

                    let failed =
                        TaskCompletionSource<int>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    let running = ActorAccess.run gate id () (fun () -> failed.Task)
                    let stopping = gate.Stop()
                    let primary = InvalidOperationException("primary callback witness")
                    failed.SetException primary
                    let mutable actual: exn option = None

                    do!
                        task {
                            try
                                let! _ = running.WaitAsync(TimeSpan.FromSeconds 2.)
                                ()
                            with error ->
                                actual <- Some error
                        }
                        |> Async.AwaitTask

                    Expect.isTrue
                        (actual |> Option.exists (fun error -> obj.ReferenceEquals(error, primary)))
                        "Original exception identity survives"

                    let! outcome = stopping.WaitAsync(TimeSpan.FromSeconds 2.) |> Async.AwaitTask

                    Expect.equal
                        outcome
                        ShutdownOutcome.TeardownFailed
                        "Both failures are represented independently"
                })
            testCaseAsync
                "actual host completion preserves primary failures and reports secondary shutdown"
                (async {
                    let primary = InvalidOperationException("primary private witness")
                    let reports = ResizeArray<ShutdownOutcome>()
                    let mutable actual: exn option = None

                    do!
                        task {
                            try
                                let! _ =
                                    ClaimCore.Server.HostProgram.withShutdown
                                        (fun () -> Task.FromException<int>(primary))
                                        (fun () -> Task.FromResult ShutdownOutcome.TeardownFailed)
                                        (fun outcome ->
                                            reports.Add outcome
                                            failwith "observer failure")

                                ()
                            with error ->
                                actual <- Some error
                        }
                        |> Async.AwaitTask

                    Expect.isTrue
                        (actual |> Option.exists (fun error -> obj.ReferenceEquals(error, primary)))
                        "Primary exception identity survives host shutdown and observer failure"

                    Expect.sequenceEqual
                        reports
                        [ ShutdownOutcome.TeardownFailed ]
                        "Secondary teardown is still reported"

                    let! result =
                        ClaimCore.Server.HostProgram.withShutdown
                            (fun () -> Task.FromResult 0)
                            (fun () -> Task.FromResult ShutdownOutcome.BoundExpired)
                            ignore
                        |> Async.AwaitTask

                    Expect.equal
                        result
                        (0, ShutdownOutcome.BoundExpired)
                        "Bound expiry reaches the real host exit mapper"
                })
        ]
