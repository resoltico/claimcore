module ClaimCore.Tests.ShellIdleTests

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open Expecto
open ShellTestClock
open ShellIdentityFixture

let private discardWhileReading () =
    task {
        let clock = Clock()
        use provider = new Provider(clock)
        use credentials = session provider
        token credentials |> ignore

        let input =
            TaskCompletionSource<ShellLine>(TaskCreationOptions.RunContinuationsAsynchronously)

        let discarded =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable discards = 0

        let discard () =
            task {
                do! credentials.Discard CancellationToken.None
                discards <- discards + 1
                discarded.TrySetResult() |> ignore
            }

        let waiting = ShellIdle.read clock (clock.GetTimestamp()) input.Task discard
        do! (clock.WhenTimer 1).WaitAsync(TimeSpan.FromSeconds 5.)
        clock.Advance(TimeSpan.FromMinutes 15. - TimeSpan.FromTicks 1L)
        Expect.equal discards 0 "No early idle discard"
        Expect.isFalse waiting.IsCompleted "One pending input read remains"
        clock.Advance(TimeSpan.FromTicks 1L)
        do! discarded.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal discards 1 "Exact idle boundary discards credentials before input arrives"
        Expect.isFalse input.Task.IsCompleted "Idle expiry does not complete or replace input"
        input.SetResult(ShellLine.Text "context")
        let! actual = waiting
        Expect.equal actual (ShellLine.Text "context") "The original input await completes"
        token credentials |> ignore
        Expect.equal provider.SignIns 2 "Next command signs in again"
        Expect.equal provider.Refreshes 0 "Idle discard includes refresh material"
        do! provider.Completed()
    }

let private inputWins () =
    task {
        let clock = Clock()

        let input =
            TaskCompletionSource<ShellLine>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable discarded = false

        let waiting =
            ShellIdle.read clock (clock.GetTimestamp()) input.Task (fun () ->
                discarded <- true
                Task.FromResult())

        do! (clock.WhenTimer 1).WaitAsync(TimeSpan.FromSeconds 5.)
        input.SetResult(ShellLine.Text "help")
        let! actual = waiting
        Expect.equal actual (ShellLine.Text "help") "Command before deadline completes"
        clock.Advance(TimeSpan.FromMinutes 30.)
        Expect.isFalse discarded "Completed prompt cancels its timer"
    }

[<Tests>]
let tests =
    testList
        "shell idle lock"
        [
            testTask "discards all credentials at fifteen minutes while one read waits" {
                do! discardWhileReading ()
            }
            testTask "completed input cancels the old idle timer" { do! inputWins () }
        ]
