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
    let reads = 4
    let recovery = 2
    let maximumWriters = 4

    let maximumQueuedWriters =
        ingress
        - maximumWriters
        - reads
        - recovery
        - cookieAuthentication
        - bearerAuthentication
        - interactiveAuthentication

    let maximumPartitions = 64
    // Every concurrently admitted storage user, including both readiness probes, needs a pool slot.
    let minimumPoolCapacity writers =
        reads + writers + recovery + sessions + readiness

/// Capacity rejection before an action must not be mistaken for an invalid or expired ticket.
type internal AdmissionRejectedException() =
    inherit Exception("Authentication storage admission is busy.")

[<RequireQualifiedAccess>]
type internal RequestWorkload =
    | Read
    | Write
    | Recovery

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
type internal PrincipalAdmission(writers, queuedWriters) =
    do
        if
            writers < 1
            || writers > AdmissionPolicy.maximumWriters
            || queuedWriters < 0
            || queuedWriters > AdmissionPolicy.maximumQueuedWriters
        then
            invalidArg
                (nameof writers)
                "Admission capacities exceed the per-process resource policy."

    let reads = AdmissionLease.limiter AdmissionPolicy.reads 0
    let writes = AdmissionLease.limiter writers queuedWriters
    let recovery = AdmissionLease.limiter AdmissionPolicy.recovery 0
    let sync = obj ()
    let mutable closed = false

    let mutable partitions: Map<PrincipalKey * RequestWorkload, PrincipalPartition> =
        Map.empty

    let capacity =
        function
        | RequestWorkload.Read -> 2
        | RequestWorkload.Write
        | RequestWorkload.Recovery -> 1

    let workload =
        function
        | RequestWorkload.Read -> reads
        | RequestWorkload.Write -> writes
        | RequestWorkload.Recovery -> recovery

    let release key partition (lease: RateLimitLease) =
        lease.Dispose()

        lock sync (fun () ->
            partition.Holders <- partition.Holders - 1

            if partition.Holders = 0 then
                partitions <- Map.remove key partitions
                partition.Limiter.Dispose())

    let reserve key =
        lock sync (fun () ->
            if closed then
                None
            else
                let available =
                    match Map.tryFind key partitions with
                    | Some value -> Some value
                    | None when partitions.Count < AdmissionPolicy.maximumPartitions ->
                        let value =
                            {
                                Limiter = AdmissionLease.limiter (capacity (snd key)) 0
                                Holders = 0
                            }

                        partitions <- Map.add key value partitions
                        Some value
                    | _ -> None

                available
                |> Option.bind (fun partition ->
                    let lease = partition.Limiter.AttemptAcquire 1

                    if lease.IsAcquired then
                        partition.Holders <- partition.Holders + 1
                        Some(AdmissionLease.once (fun () -> release key partition lease))
                    else
                        lease.Dispose()
                        None))

    member _.ActivePartitions = lock sync (fun () -> partitions.Count)
    member _.QueuedWriters = (writes.GetStatistics() |> nonNull).CurrentQueuedCount

    member _.Acquire(principal, kind, token: CancellationToken) =
        task {
            match reserve (principal, kind) with
            | None -> return None
            | Some owner ->
                try
                    let! lease = (workload kind).AcquireAsync(1, token)

                    if lease.IsAcquired then
                        return
                            Some(
                                AdmissionLease.once (fun () ->
                                    try
                                        lease.Dispose()
                                    finally
                                        owner.Dispose())
                            )
                    else
                        lease.Dispose()
                        owner.Dispose()
                        return None
                with error ->
                    owner.Dispose()
                    return raise error
        }

    interface IDisposable with
        member _.Dispose() =
            lock sync (fun () -> closed <- true)
            reads.Dispose()
            writes.Dispose()
            recovery.Dispose()
