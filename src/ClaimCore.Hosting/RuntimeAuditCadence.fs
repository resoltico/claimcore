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
    let mutable disposed = false
    let mutable failed = 0
    let mutable lastCompletedTicks = clock.Timestamp()

    let worker =
        task {
            try
                use timer = new PeriodicTimer(interval)
                let mutable running = true

                while running do
                    let! due = timer.WaitForNextTickAsync(cancellation.Token)

                    if due then
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
                            Interlocked.Exchange(&failed, 1) |> ignore
                            running <- false
                    else
                        running <- false
            with
            | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            | _ -> Interlocked.Exchange(&failed, 1) |> ignore
        }

    member _.RequireHealthy() =
        if Volatile.Read(&failed) <> 0 then
            invalidOp "Scheduled full audit failed; case-work admission is quarantined."

        let elapsed = clock.Elapsed(Volatile.Read(&lastCompletedTicks))

        if elapsed > interval + TimeSpan.FromHours 2. then
            Interlocked.Exchange(&failed, 1) |> ignore
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
        member _.Dispose() =
            lock disposalGate (fun () ->
                if not disposed then
                    try
                        cancellation.Cancel()

                        try
                            worker.GetAwaiter().GetResult()
                        with :? OperationCanceledException ->
                            ()
                    finally
                        cancellation.Dispose()
                        disposed <- true)

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
