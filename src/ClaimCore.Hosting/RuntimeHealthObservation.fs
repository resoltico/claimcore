namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks

/// Reporting only. Sampling never supplies actor authority or replaces a live admission check.
type internal RuntimeHealthObservation(?timeProvider: TimeProvider) =
    let clock = defaultArg timeProvider TimeProvider.System
    let gate = obj ()
    let cancellation = new CancellationTokenSource()
    let mutable epoch = 0L
    let mutable closed = false
    let mutable observation: (int64 * int64 * (string * string * bool)) option = None
    let mutable worker: Task option = None
    let mutable callbacks: Task option = None
    let unknown = "UNKNOWN", "QUARANTINED", false

    let publish generation started value =
        lock gate (fun () ->
            if not closed && epoch = generation then
                observation <- Some(generation, started, value))

    let sample observe =
        task {
            let generation, started = lock gate (fun () -> epoch, clock.GetTimestamp())
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5., clock)

            use budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token)

            use _expired = budget.Token.Register(fun () -> publish generation started unknown)

            try
                let! value = observe budget.Token

                lock gate (fun () ->
                    if not closed && epoch = generation && not budget.IsCancellationRequested then
                        observation <- Some(generation, started, value))
            with _ ->
                publish generation started unknown
        }

    member _.Invalidate() =
        lock gate (fun () ->
            epoch <- epoch + 1L
            observation <- None)

    member _.Read() =
        lock gate (fun () ->
            match observation with
            | Some(generation, started, value) when not closed && generation = epoch ->
                let age = clock.GetElapsedTime(started)

                if age >= TimeSpan.Zero && age < TimeSpan.FromSeconds 15. then
                    value
                else
                    unknown
            | _ -> unknown)

    member _.Start(observe: CancellationToken -> Task<string * string * bool>) =
        lock gate (fun () ->
            if closed || worker.IsSome then
                invalidOp "Health sampling cannot start."

            worker <-
                Some(
                    Task.Run(fun () ->
                        task {
                            try
                                while not cancellation.IsCancellationRequested do
                                    do! sample observe
                                    // A delayed sample owns its work until it ends; missed ticks coalesce.
                                    do!
                                        Task.Delay(
                                            TimeSpan.FromSeconds 5.,
                                            clock,
                                            cancellation.Token
                                        )
                            with :? OperationCanceledException when
                                cancellation.IsCancellationRequested ->
                                ()
                        }
                        :> Task)
                ))

    member _.RequestStop() =
        lock gate (fun () ->
            if not closed then
                closed <- true
                epoch <- epoch + 1L
                observation <- None
                callbacks <- Some(cancellation.CancelAsync()))

    interface IDisposable with
        member this.Dispose() =
            this.RequestStop()
            let pending, stopping = lock gate (fun () -> worker, callbacks)
            pending |> Option.iter (fun task -> task.GetAwaiter().GetResult())
            stopping |> Option.iter (fun task -> task.GetAwaiter().GetResult())
            cancellation.Dispose()
