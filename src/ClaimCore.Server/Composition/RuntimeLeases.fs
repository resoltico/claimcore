namespace ClaimCore.Server.Composition

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ShutdownOutcome =
    | Drained
    | BoundExpired
    | TeardownFailed

/// Leases cover admitted work through its cleanup. Teardown runs once outside the admission lock.
type internal RuntimeLeases(close: unit -> unit) =
    let sync = obj ()

    let drained =
        TaskCompletionSource<ShutdownOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable active = 0
    let mutable closing = false
    let mutable teardownStarted = false

    let beginTeardown () =
        if closing && active = 0 && not teardownStarted then
            teardownStarted <- true
            true
        else
            false

    let teardown () =
        Task.Run(
            Action(fun () ->
                let outcome =
                    try
                        close ()
                        ShutdownOutcome.Drained
                    with _ ->
                        ShutdownOutcome.TeardownFailed

                drained.TrySetResult outcome |> ignore)
        )
        |> ignore

    member _.Acquire() =
        lock sync (fun () ->
            if closing then
                None
            else
                active <- active + 1
                let mutable released = false

                Some
                    { new IDisposable with
                        member _.Dispose() =
                            let finish =
                                lock sync (fun () ->
                                    if released then
                                        false
                                    else
                                        released <- true
                                        active <- active - 1
                                        beginTeardown ())

                            if finish then
                                teardown ()
                    })

    member _.Stop() =
        let finish =
            lock sync (fun () ->
                closing <- true
                beginTeardown ())

        if finish then
            teardown ()

        drained.Task

    member this.Shutdown(bound: TimeSpan) =
        task {
            if bound < TimeSpan.Zero || bound.TotalMilliseconds > float Int32.MaxValue then
                invalidArg (nameof bound) "Use a finite nonnegative shutdown bound."

            let drain = this.Stop()
            use timer = new CancellationTokenSource()
            let! finished = Task.WhenAny(drain :> Task, Task.Delay(bound, timer.Token))

            if obj.ReferenceEquals(finished, drain) then
                timer.Cancel()
                return! drain
            else
                return ShutdownOutcome.BoundExpired
        }
