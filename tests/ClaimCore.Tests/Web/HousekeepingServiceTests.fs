module ClaimCore.Tests.HousekeepingServiceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Server

[<Tests>]
let tests =
    testCaseAsync
        "host housekeeping timer runs without requests and stops cleanly"
        (async {
            let clock = ShellTestClock.Clock()

            let called =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let mutable calls = 0

            use service =
                new HousekeepingService(
                    (fun _ ->
                        task {
                            calls <- calls + 1
                            called.TrySetResult() |> ignore
                        }),
                    clock
                )

            do! service.StartAsync(CancellationToken.None) |> Async.AwaitTask
            do! (clock.WhenTimer 1).WaitAsync(TimeSpan.FromSeconds 3.) |> Async.AwaitTask
            Expect.equal calls 0 "Startup does not create an unbounded cleanup burst"
            clock.Advance(TimeSpan.FromMinutes 12.)
            do! called.Task.WaitAsync(TimeSpan.FromSeconds 3.) |> Async.AwaitTask
            do! (clock.WhenTimer 2).WaitAsync(TimeSpan.FromSeconds 3.) |> Async.AwaitTask
            Expect.equal calls 1 "The scheduled timer, not an actor request, invokes housekeeping"
            do! service.StopAsync(CancellationToken.None) |> Async.AwaitTask
            clock.Advance(TimeSpan.FromMinutes 12.)
            Expect.equal calls 1 "Shutdown cancels the next timer"
        })
