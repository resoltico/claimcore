module ClaimCore.WebTests.RequestAdmissionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Web

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
        use! _first = acquire admission "noisy" RequestWorkload.Read |> admitted
        use! _second = acquire admission "noisy" RequestWorkload.Read |> admitted
        let! refused = acquire admission "noisy" RequestWorkload.Read
        Expect.isNone refused "One identity cannot occupy all ordinary reads"
        use! _normal = acquire admission "normal" RequestWorkload.Read |> admitted
        use! _recovery = acquire admission "normal" RequestWorkload.Recovery |> admitted
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
            use! status = acquire admission "status" RequestWorkload.Recovery |> admitted

            for owner in owners do
                owner.Dispose()

            for pending in queued do
                let! owner = pending
                (require owner).Dispose()

            read.Dispose()
            status.Dispose()

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

[<Tests>]
let tests =
    testList
        "request admission"
        [
            testCaseAsync
                "isolates a noisy canonical principal across work classes"
                (async { do! isolation () |> Async.AwaitTask })
            testCaseAsync
                "bounds queued writers while preserving read and passive recovery capacity"
                (async { do! boundedQueue () |> Async.AwaitTask })
            testCaseAsync
                "holds a cancellation-ignoring provider through cleanup"
                (async { do! providerLifetime () |> Async.AwaitTask })
            testCaseAsync
                "discards completed principal partitions"
                (async { do! identities () |> Async.AwaitTask })
        ]
