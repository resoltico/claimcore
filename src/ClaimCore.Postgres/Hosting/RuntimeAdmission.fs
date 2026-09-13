namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks

/// Closes admission before draining; a timed-out disposer leaves final cleanup to the last lease.
type internal RuntimeAdmission(dataSource: IDisposable, drainTimeout: TimeSpan) =
    let gate = obj ()

    let drained =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closing = false
    let mutable active = 0
    let mutable cleanupStarted = false

    let cleanup () =
        Task.Run(fun () ->
            try
                dataSource.Dispose()
                drained.TrySetResult() |> ignore
            with error ->
                drained.TrySetException(error) |> ignore)
        |> ignore

    let release () =
        let shouldCleanup =
            lock gate (fun () ->
                active <- active - 1

                if closing && active = 0 && not cleanupStarted then
                    cleanupStarted <- true
                    true
                else
                    false)

        if shouldCleanup then
            cleanup ()

    do
        if isNull (box dataSource) then
            nullArg (nameof dataSource)

        if drainTimeout <= TimeSpan.Zero then
            invalidArg (nameof drainTimeout) "The runtime drain timeout must be positive."

    member _.Admit() =
        lock gate (fun () ->
            if closing then
                raise (ObjectDisposedException("ClaimCore Runtime"))

            active <- active + 1
            let mutable released = 0

            { new IDisposable with
                member _.Dispose() =
                    if Interlocked.Exchange(&released, 1) = 0 then
                        release ()
            })

    member this.Run(work: unit -> Task<'value>) =
        task {
            use _lease = this.Admit()
            return! work ()
        }

    interface IDisposable with
        member _.Dispose() =
            let shouldCleanup =
                lock gate (fun () ->
                    closing <- true

                    if active = 0 && not cleanupStarted then
                        cleanupStarted <- true
                        true
                    else
                        false)

            if shouldCleanup then
                cleanup ()

            try
                if drained.Task.Wait(drainTimeout) then
                    drained.Task.GetAwaiter().GetResult()
            with _ ->
                raise (InvalidOperationException("ClaimCore runtime cleanup failed."))
