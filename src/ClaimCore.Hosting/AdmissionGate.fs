namespace ClaimCore.Hosting

open System
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ShutdownOutcome =
    | Drained
    | BoundExpired

/// Closing admission never cancels or reclassifies work that already holds a lease.
type internal AdmissionGate(close: unit -> unit) =
    let sync = obj ()

    let drained =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable active = 0
    let mutable closing = false
    let mutable disposed = false

    let dispose () =
        if not disposed then
            disposed <- true
            close ()

    let complete () =
        dispose ()
        drained.TrySetResult() |> ignore

    let release () =
        lock sync (fun () ->
            active <- active - 1

            if closing && active = 0 then
                complete ())

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
                            lock sync (fun () ->
                                if not released then
                                    released <- true
                                    release ())
                    })

    member _.Stop() =
        lock sync (fun () ->
            closing <- true

            if active = 0 then
                complete ()

            drained.Task)

    member this.Shutdown(bound: TimeSpan) =
        task {
            if bound < TimeSpan.Zero || bound.TotalMilliseconds > float Int32.MaxValue then
                invalidArg (nameof bound) "Use a finite nonnegative shutdown bound."

            let drain = this.Stop()
            let! finished = Task.WhenAny(drain, Task.Delay bound)

            return
                if obj.ReferenceEquals(finished, drain) then
                    ShutdownOutcome.Drained
                else
                    lock sync dispose
                    ShutdownOutcome.BoundExpired
        }
