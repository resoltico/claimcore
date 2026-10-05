namespace ClaimCore.Hosting

open System
open System.Diagnostics
open System.Globalization
open System.Threading
open System.Threading.Tasks

/// Periodic whole-installation reconciliation. A failed or overdue audit closes case-work
/// admission until the runtime is reopened after owner reconciliation.
[<NoEquality; NoComparison>]
type internal RuntimeAuditClock =
    {
        Timestamp: unit -> int64
        Elapsed: int64 -> TimeSpan
    }

type internal RuntimeAuditCadence
    (runAudit: CancellationToken -> Task, interval: TimeSpan, clock: RuntimeAuditClock) =
    let interval =
        if interval <= TimeSpan.Zero || interval > TimeSpan.FromHours 24. then
            invalidArg (nameof interval) "Full-audit interval must be positive and at most one day."

        interval

    let cancellation = new CancellationTokenSource()
    let disposalGate = obj ()
    let stopGate = obj ()
    let mutable stopTask: Task option = None
    let mutable disposed = false
    let mutable failed = 0
    let mutable lastCompletedTicks = clock.Timestamp()

    let quarantine overdue =
        if Interlocked.Exchange(&failed, 1) = 0 then
            try
                RuntimeOperationalSignals.audit overdue
            with _ ->
                ()

    let worker =
        task {
            try
                let mutable running = true

                while running do
                    do! Task.Delay(interval, cancellation.Token)

                    try
                        use bounded =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token)

                        bounded.CancelAfter(TimeSpan.FromHours 2.)
                        do! runAudit bounded.Token
                        Interlocked.Exchange(&lastCompletedTicks, clock.Timestamp()) |> ignore
                    with
                    | :? OperationCanceledException when cancellation.IsCancellationRequested ->
                        running <- false
                    | _ ->
                        quarantine false
                        running <- false
            with
            | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            | _ -> quarantine false
        }

    member _.RequestStop() =
        lock stopGate (fun () ->
            if not disposed && stopTask.IsNone then
                stopTask <- Some(cancellation.CancelAsync()))

    member _.RequireHealthy() =
        if Volatile.Read(&failed) <> 0 then
            invalidOp "Scheduled full audit failed; case-work admission is quarantined."

        let elapsed = clock.Elapsed(Volatile.Read(&lastCompletedTicks))

        if elapsed > interval + TimeSpan.FromHours 2. then
            quarantine true
            invalidOp "Scheduled full audit is overdue; case-work admission is quarantined."

    member _.Completion = worker :> Task

    new(runAudit: CancellationToken -> Task, interval: TimeSpan) =
        new RuntimeAuditCadence(
            runAudit,
            interval,
            {
                Timestamp = Stopwatch.GetTimestamp
                Elapsed = Stopwatch.GetElapsedTime
            }
        )

    new(resources: RuntimeResources, interval: TimeSpan) =
        new RuntimeAuditCadence(
            (fun token ->
                task {
                    let! _ = RuntimeFullAudit.run resources token
                    return ()
                }
                :> Task),
            interval
        )

    interface IDisposable with
        member this.Dispose() =
            lock disposalGate (fun () ->
                let shouldDispose = lock stopGate (fun () -> not disposed)

                if shouldDispose then
                    this.RequestStop()
                    let callbacks = lock stopGate (fun () -> stopTask.Value)

                    try
                        try
                            worker.GetAwaiter().GetResult()
                        with :? OperationCanceledException ->
                            ()

                        callbacks.GetAwaiter().GetResult()
                    finally
                        lock stopGate (fun () ->
                            cancellation.Dispose()
                            disposed <- true))

module internal RuntimeAuditInterval =
    let configured () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS") with
        | null
        | "" -> TimeSpan.FromHours 6.
        | text ->
            match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, seconds when seconds >= 60 && seconds <= 86400 ->
                TimeSpan.FromSeconds(float seconds)
            | _ -> invalidOp "Full-audit interval must be 60–86400 seconds."
