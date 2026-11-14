module ClaimCore.ServerTests.RequestAdmissionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Domain
open ClaimCore.Server

let private principal name =
    PrincipalKey.human "https://issuer.example/" name
    |> Result.defaultWith (string >> failtest)

let private admitted taskValue =
    task {
        let! value = taskValue in

        return
            (match value with
             | Some lease -> lease
             | None -> failtest "Expected admission")
    }

let private require =
    function
    | Some lease -> lease
    | None -> failtest "Expected admission"

let private acquire (admission: PrincipalAdmission) name workload =
    admission.Acquire(principal name, workload, CancellationToken.None)

let private isolation () =
    task {
        use admission = new PrincipalAdmission(4, 8)
        use! first = acquire admission "noisy" RequestWorkload.Read |> admitted
        use! _second = acquire admission "noisy" RequestWorkload.Read |> admitted
        let queued = acquire admission "noisy" RequestWorkload.Read
        Expect.isFalse queued.IsCompleted "A third read waits in its principal FIFO"
        use! _normal = acquire admission "normal" RequestWorkload.Read |> admitted
        first.Dispose()
        use! _promoted = queued |> admitted
        use! _writer = acquire admission "normal" RequestWorkload.Write |> admitted
        let! duplicate = acquire admission "normal" RequestWorkload.Write

        Expect.isNone
            duplicate
            "Operation IDs and endpoint headers cannot enlarge one principal's budget"
    }

let private boundedQueue () =
    task {
        use admission = new PrincipalAdmission(4, 8)

        let! initial =
            [| for n in 1..4 -> acquire admission (string n) RequestWorkload.Write |]
            |> Task.WhenAll

        let owners = Array.map require initial
        use cancellation = new CancellationTokenSource()

        let cancelled =
            admission.Acquire(principal "cancelled", RequestWorkload.Write, cancellation.Token)

        let queued =
            [| for n in 5..11 -> acquire admission (string n) RequestWorkload.Write |]

        try
            let! excess = acquire admission "excess" RequestWorkload.Write
            Expect.isNone excess "Four holders plus eight queued requests is a hard bound"

            Expect.equal
                admission.QueuedWriters
                8L
                "The provider queue contains exactly the admitted bound"

            Expect.equal
                admission.ActivePartitions
                12
                "Only current holders and queued writers own partitions"

            cancellation.Cancel()

            let! cancelledAsExpected =
                task {
                    try
                        let! _ = cancelled in return false
                    with :? OperationCanceledException ->
                        return true
                }

            Expect.isTrue cancelledAsExpected "A queued disconnect cannot reach the action"
            use! read = acquire admission "reader" RequestWorkload.Read |> admitted

            for owner in owners do
                owner.Dispose()

            for pending in queued do
                let! owner = pending
                (require owner).Dispose()

            read.Dispose()

            Expect.equal
                admission.ActivePartitions
                0
                "Queues and partitions are released after actual completion"
        finally
            for owner in owners do
                owner.Dispose()
    }

let private providerLifetime () =
    task {
        use admission = new PrincipalAdmission(1, 0)
        use cancelled = new CancellationTokenSource()

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let cleanup =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let action =
            task {
                let! accepted =
                    admission.Acquire(principal "held", RequestWorkload.Write, cancelled.Token)

                use _lease = require accepted
                entered.SetResult()
                // A provider can ignore cancellation while it finishes transaction cleanup.
                do! cleanup.Task
            }

        do! entered.Task
        cancelled.Cancel()
        let! stillHeld = acquire admission "held" RequestWorkload.Write
        Expect.isNone stillHeld "Cancellation alone cannot release provider capacity"
        cleanup.SetResult()
        do! action
        use! _next = acquire admission "held" RequestWorkload.Write |> admitted
        Expect.equal admission.ActivePartitions 1 "Capacity is reusable after cleanup"
    }

let private identities () =
    task {
        use admission = new PrincipalAdmission(4, 0)

        for n in 1..2048 do
            let! accepted = acquire admission (string n) RequestWorkload.Read
            use _lease = require accepted

            Expect.equal
                admission.ActivePartitions
                1
                "Completed identities do not accumulate cache entries"

        Expect.equal admission.ActivePartitions 0 "No completed identity remains resident"
    }

let private responseLifetime () =
    task {
        use admission = new PrincipalAdmission(1, 0)
        let services = ServiceCollection()
        services.AddScoped<RequestCapacity>() |> ignore
        use provider = services.BuildServiceProvider()
        let scope = provider.CreateScope()

        try
            let! lease = acquire admission "response" RequestWorkload.Write
            scope.ServiceProvider.GetRequiredService<RequestCapacity>().Own(require lease)
            let! whileWriting = acquire admission "response" RequestWorkload.Write

            Expect.isNone
                whileWriting
                "Returning a handler result does not release the request's deferred response capacity"
        finally
            scope.Dispose()

        use! _next = acquire admission "response" RequestWorkload.Write |> admitted
        Expect.equal admission.ActivePartitions 1 "Scope cleanup makes response capacity reusable"
    }

let private readQueue () =
    task {
        use admission = new PrincipalAdmission(4, 8)
        use! first = acquire admission "queued-reader" RequestWorkload.Read |> admitted
        use! second = acquire admission "queued-reader" RequestWorkload.Read |> admitted
        use cancellation = new CancellationTokenSource()

        let pending =
            [|
                for index in 0..7 ->
                    admission.Acquire(
                        principal "queued-reader",
                        RequestWorkload.Read,
                        if index = 4 then
                            cancellation.Token
                        else
                            CancellationToken.None
                    )
            |]

        let! excess = acquire admission "queued-reader" RequestWorkload.Read
        Expect.isNone excess "Eight queued reads is the principal bound"

        Expect.isTrue
            (pending |> Array.forall (fun request -> not request.IsCompleted))
            "Both active reads retain the queue"

        use! other = acquire admission "other-reader" RequestWorkload.Read |> admitted
        first.Dispose()
        use! promoted = pending[0] |> admitted
        Expect.isFalse pending[1].IsCompleted "One released permit promotes only the oldest request"
        cancellation.Cancel()

        let! cancelled =
            task {
                try
                    let! _ = pending[4]
                    return false
                with :? OperationCanceledException ->
                    return true
            }

        Expect.isTrue cancelled "Queued disconnects are removed without dispatch"
        promoted.Dispose()

        for index in [ 1; 2; 3; 5; 6; 7 ] do
            use! next = pending[index] |> admitted

            for later in index + 1 .. 7 do
                if later <> 4 then
                    Expect.isFalse
                        pending[later].IsCompleted
                        "Queue processing remains FIFO after cancellation"

            next.Dispose()

        second.Dispose()
        other.Dispose()
        Expect.equal admission.ActivePartitions 0 "Active and queued owner references are released"
    }

let private readTimeout () =
    task {
        use admission = new PrincipalAdmission(4, 0)
        use! _first = acquire admission "timeout" RequestWorkload.Read |> admitted
        use! _second = acquire admission "timeout" RequestWorkload.Read |> admitted
        let! timedOut = acquire admission "timeout" RequestWorkload.Read
        Expect.isNone timedOut "The five-second acquire deadline refuses a blocked read"
        Expect.equal admission.ActiveReads 2L "A timed-out read never acquires a storage permit"
    }

[<Tests>]
let tests =
    testList
        "request admission"
        [
            testCaseAsync
                "queues eight reads FIFO and cancels queued disconnects"
                (async { do! readQueue () |> Async.AwaitTask })
            testCaseAsync
                "expires queued reads after five seconds"
                (async { do! readTimeout () |> Async.AwaitTask })
            testCaseAsync
                "isolates a noisy canonical principal across work classes"
                (async { do! isolation () |> Async.AwaitTask })
            testCaseAsync
                "bounds queued writers while preserving read capacity"
                (async { do! boundedQueue () |> Async.AwaitTask })
            testCaseAsync
                "holds a cancellation-ignoring provider through cleanup"
                (async { do! providerLifetime () |> Async.AwaitTask })
            testCaseAsync
                "discards completed principal partitions"
                (async { do! identities () |> Async.AwaitTask })
            testCaseAsync
                "owns workload capacity through deferred response writing"
                (async { do! responseLifetime () |> Async.AwaitTask })
        ]
