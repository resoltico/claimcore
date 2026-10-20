module ClaimCore.WebTests.ReadinessTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open Microsoft.AspNetCore.Http

let private capacity () =
    task {
        let completion =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable calls = 0

        let ready =
            Readiness(
                (fun _ ->
                    calls <- calls + 1
                    completion.Task),
                CancellationToken.None
            )

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()
        let! prior = ready.Check cancelled.Token
        Expect.isFalse prior "Pre-cancelled request refuses"
        Expect.equal calls 0 "No provider work"
        let first = ready.Check CancellationToken.None
        let second = ready.Check CancellationToken.None
        let! third = ready.Check CancellationToken.None
        Expect.isFalse third "No queue"
        do! Task.Delay 2200
        let! expired = ready.Check CancellationToken.None
        Expect.isFalse expired "Cancellation-ignoring providers still own both permits"
        Expect.equal calls 2 "No detached extra work"
        completion.SetResult true
        let! finished = Task.WhenAll(first, second)
        Expect.isTrue (finished |> Array.forall not) "Deadline never reports ready"
        let! recovered = ready.Check CancellationToken.None
        Expect.isTrue recovered "Completed cleanup returns capacity"
    }

let private cancellation () =
    task {
        let ready =
            Readiness(
                (fun token ->
                    task {
                        do! Task.Delay(Timeout.Infinite, token)
                        return true
                    }),
                CancellationToken.None
            )

        let watch = Diagnostics.Stopwatch.StartNew()
        let! result = ready.Check CancellationToken.None
        Expect.isFalse result "Deadline cancels provider"
        Expect.isLessThan watch.Elapsed (TimeSpan.FromSeconds 5.) "Measured synthetic cleanup bound"
    }

let private httpCapacity () =
    task {
        let completion =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let ready = Readiness((fun _ -> completion.Task), CancellationToken.None)

        let context path =
            let value = DefaultHttpContext()
            value.Request.Method <- "GET"
            value.Request.Path <- PathString path
            value

        let invoke context =
            HostRoutes.health ready.Check context (RequestDelegate(fun _ -> Task.CompletedTask))

        let first = invoke (context "/health/ready")
        let second = invoke (context "/health/ready")
        let third = context "/health/ready"
        do! invoke third
        Expect.equal third.Response.StatusCode 503 "Saturation refuses promptly"
        let live = context "/health/live"
        do! invoke live
        Expect.equal live.Response.StatusCode 200 "Liveness is independent"
        completion.SetResult true
        do! Task.WhenAll(first, second)
    }

let private releasedCapacity () =
    task {
        let mutable calls = 0

        let probe token =
            task {
                calls <- calls + 1

                if calls = 1 then
                    return invalidOp "Synthetic provider failure"
                elif calls = 2 then
                    do! Task.Delay(Timeout.Infinite, token)
                    return true
                else
                    return true
            }

        use stopping = new CancellationTokenSource()
        let ready = Readiness(probe, stopping.Token)
        let! failed = ready.Check CancellationToken.None
        Expect.isFalse failed "Provider error refuses"
        use aborted = new CancellationTokenSource()
        let active = ready.Check aborted.Token
        aborted.Cancel()
        let! cancelled = active
        Expect.isFalse cancelled "Request abort reaches provider"
        let! recovered = ready.Check CancellationToken.None
        Expect.isTrue recovered "Error and abort return capacity after cleanup"
        stopping.Cancel()
        let! stopped = ready.Check CancellationToken.None
        Expect.isFalse stopped "Stopping refuses before provider admission"
        Expect.equal calls 3 "No work starts while stopping"
    }

[<Tests>]
let tests =
    testList
        "bounded readiness"
        [
            testCaseAsync
                "provider error request abort and stopping preserve permit lifetime"
                (async { do! releasedCapacity () |> Async.AwaitTask })
            testCaseAsync
                "HTTP saturation returns 503 while liveness remains independent"
                (async { do! httpCapacity () |> Async.AwaitTask })
            testCaseAsync
                "capacity remains owned through cancellation-ignoring provider cleanup"
                (async { do! capacity () |> Async.AwaitTask })
            testCaseAsync
                "deadline reaches cancellation-aware provider"
                (async { do! cancellation () |> Async.AwaitTask })
        ]
