namespace ClaimCore.Web

open System
open System.Threading
open System.Threading.RateLimiting
open ClaimCore.Domain

/// Per-process resource policy. Queued writers cannot consume every ingress slot.
module internal AdmissionPolicy =
    let ingress = 24
    let cookieAuthentication = 2
    let bearerAuthentication = 2
    let interactiveAuthentication = 2
    let sessions = 4
    let readiness = 2
    let housekeeping = 1
    let maximumCorePermits = 4

    let maximumQueuedWriters = 8

    let maximumPartitions = 64
    // Every concurrently admitted storage user, including both readiness probes, needs a pool slot.
    let minimumPoolCapacity permits =
        2 * permits + sessions + readiness + housekeeping

/// Capacity rejection before an action must not be mistaken for an invalid or expired ticket.
type internal AdmissionRejectedException() =
    inherit Exception("Authentication storage admission is busy.")

/// No action callback has started when a workload permit cannot be acquired.
type internal WorkloadAdmissionRejectedException() =
    inherit Exception("Storage workload admission is busy.")

[<RequireQualifiedAccess>]
type internal RequestWorkload =
    | Read
    | Write

module internal AdmissionLease =
    let once release =
        let mutable disposed = 0

        { new IDisposable with
            member _.Dispose() =
                if Interlocked.Exchange(&disposed, 1) = 0 then
                    release ()
        }

    let limiter permits queued =
        new ConcurrencyLimiter(
            ConcurrencyLimiterOptions(
                PermitLimit = permits,
                QueueLimit = queued,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            )
        )

/// Minimal API results execute after the handler returns. The request scope owns their permit.
type internal RequestCapacity() =
    let mutable owned: IDisposable option = None

    member _.Own(lease: IDisposable) =
        match owned with
        | Some _ -> invalidOp "A request already owns its workload capacity."
        | None -> owned <- Some lease

    interface IDisposable with
        member _.Dispose() =
            owned |> Option.iter _.Dispose()
            owned <- None

[<NoEquality; NoComparison>]
type private PrincipalPartition =
    {
        Limiter: ConcurrencyLimiter
        mutable Holders: int
    }

/// Canonical authenticated identities own only live admissions, never an unbounded retained cache.
type internal PrincipalAdmission(permits, queuedWriters) =
    do
        if
            permits < 1
            || permits > AdmissionPolicy.maximumCorePermits
            || queuedWriters < 0
            || queuedWriters > AdmissionPolicy.maximumQueuedWriters
        then
            invalidArg
                (nameof permits)
                "Admission capacities exceed the per-process resource policy."

    // The configured core permit count bounds each process storage workload.
    let reads = AdmissionLease.limiter permits 0
    let writes = AdmissionLease.limiter permits queuedWriters
    let sync = obj ()
    let mutable closed = false

    let mutable partitions: Map<PrincipalKey * RequestWorkload, PrincipalPartition> =
        Map.empty

    let capacity =
        function
        | RequestWorkload.Read -> 2
        | RequestWorkload.Write -> 1

    let workload =
        function
        | RequestWorkload.Read -> reads
        | RequestWorkload.Write -> writes

    let release key partition =
        lock sync (fun () ->
            partition.Holders <- partition.Holders - 1

            if partition.Holders = 0 then
                partitions <- Map.remove key partitions
                partition.Limiter.Dispose())

    // Queued requests own a reference too: releasing an active request must not dispose their FIFO.
    let reserve key =
        lock sync (fun () ->
            if closed then
                None
            else
                let available =
                    match Map.tryFind key partitions with
                    | Some value -> Some value
                    | None when partitions.Count < AdmissionPolicy.maximumPartitions ->
                        let queued = if snd key = RequestWorkload.Read then 8 else 0

                        let value =
                            {
                                Limiter = AdmissionLease.limiter (capacity (snd key)) queued
                                Holders = 0
                            }

                        partitions <- Map.add key value partitions
                        Some value
                    | _ -> None

                available
                |> Option.map (fun partition ->
                    partition.Holders <- partition.Holders + 1
                    partition, AdmissionLease.once (fun () -> release key partition)))

    member _.ActivePartitions = lock sync (fun () -> partitions.Count)

    member _.ActiveReads =
        int64 permits - (reads.GetStatistics() |> nonNull).CurrentAvailablePermits

    member _.QueuedReads =
        lock sync (fun () ->
            partitions
            |> Map.toSeq
            |> Seq.sumBy (fun ((_, kind), partition) ->
                if kind = RequestWorkload.Read then
                    (partition.Limiter.GetStatistics() |> nonNull).CurrentQueuedCount
                else
                    0L))

    member _.QueuedWriters = (writes.GetStatistics() |> nonNull).CurrentQueuedCount

    member _.Acquire(principal, kind, token: CancellationToken) =
        task {
            match reserve (principal, kind) with
            | None -> return None
            | Some(partition, owner) ->
                use waiting = CancellationTokenSource.CreateLinkedTokenSource token

                if kind = RequestWorkload.Read then
                    waiting.CancelAfter(TimeSpan.FromSeconds 5.0)

                try
                    let! principalLease = partition.Limiter.AcquireAsync(1, waiting.Token)

                    if not principalLease.IsAcquired then
                        principalLease.Dispose()
                        owner.Dispose()
                        return None
                    else
                        try
                            let! lease = (workload kind).AcquireAsync(1, waiting.Token)

                            if lease.IsAcquired then
                                return
                                    Some(
                                        AdmissionLease.once (fun () ->
                                            try
                                                lease.Dispose()
                                            finally
                                                principalLease.Dispose()
                                                owner.Dispose())
                                    )
                            else
                                lease.Dispose()
                                principalLease.Dispose()
                                owner.Dispose()
                                return None
                        with error ->
                            principalLease.Dispose()
                            return raise error
                with
                | :? OperationCanceledException when not token.IsCancellationRequested ->
                    owner.Dispose()
                    return None
                | error ->
                    owner.Dispose()
                    return raise error
        }

    interface IDisposable with
        member _.Dispose() =
            lock sync (fun () -> closed <- true)
            reads.Dispose()
            writes.Dispose()

            lock sync (fun () ->
                for KeyValue(_, partition) in partitions do
                    partition.Limiter.Dispose())
