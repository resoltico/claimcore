module ClaimCore.Tests.ShellTestClock

open System
open System.Threading
open System.Collections.Generic
open System.Threading.Tasks

[<NoEquality; NoComparison>]
type private Scheduled =
    {
        Callback: TimerCallback
        State: obj | null
        mutable Due: int64
        mutable Period: int64
        mutable Disposed: bool
    }

type Clock() =
    inherit TimeProvider()
    let gate = obj ()
    let mutable utc = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
    let mutable timestamp = 0L
    let scheduled = ResizeArray<Scheduled>()
    let watchers = Dictionary<int, TaskCompletionSource<unit>>()

    let ticks (duration: TimeSpan) =
        if duration = Timeout.InfiniteTimeSpan then
            Int64.MaxValue
        else
            duration.Ticks

    let due duration =
        let value = ticks duration
        if value = Int64.MaxValue then value else timestamp + value

    override _.GetUtcNow() = lock gate (fun () -> utc)
    override _.GetTimestamp() = lock gate (fun () -> timestamp)
    override _.TimestampFrequency = TimeSpan.TicksPerSecond

    member _.WhenTimer(count) =
        lock gate (fun () ->
            if scheduled.Count >= count then
                Task.CompletedTask
            else
                let signal =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                watchers.Add(count, signal)
                signal.Task :> Task)

    member _.Advance(duration: TimeSpan) =
        let expired =
            lock gate (fun () ->
                utc <- utc.Add duration
                timestamp <- timestamp + duration.Ticks

                scheduled
                |> Seq.filter (fun timer -> not timer.Disposed && timer.Due <= timestamp)
                |> Seq.map (fun timer ->
                    timer.Due <-
                        if timer.Period = Int64.MaxValue then
                            Int64.MaxValue
                        else
                            timestamp + timer.Period

                    timer.Callback, timer.State)
                |> Seq.toArray)

        for callback, state in expired do
            callback.Invoke state

    override _.CreateTimer(callback, state, dueTime, period) =
        let timer =
            lock gate (fun () ->
                let value =
                    {
                        Callback = callback
                        State = state
                        Due = due dueTime
                        Period = ticks period
                        Disposed = false
                    }

                scheduled.Add value

                match watchers.TryGetValue scheduled.Count with
                | true, signal -> signal.TrySetResult() |> ignore
                | _ -> ()

                value)

        let dispose () =
            lock gate (fun () -> timer.Disposed <- true)

        { new ITimer with
            member _.Change(dueTime, period) =
                lock gate (fun () ->
                    if timer.Disposed then
                        false
                    else
                        timer.Due <- due dueTime
                        timer.Period <- ticks period
                        true)
          interface IDisposable with
              member _.Dispose() = dispose ()
          interface IAsyncDisposable with
              member _.DisposeAsync() =
                  dispose ()
                  ValueTask.CompletedTask
        }
