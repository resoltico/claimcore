module ClaimCore.IntegrationTests.AuditCancellationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting

let private cancelledError (token: CancellationToken) =
    try
        Task.Delay(Timeout.InfiniteTimeSpan, token).GetAwaiter().GetResult()
        failtest "Synthetic cancellation did not occur."
    with :? OperationCanceledException as error ->
        error :> exn

let private distinguishCauses () =
    use shutdown = new CancellationTokenSource()
    use deadline = new CancellationTokenSource()

    use linked =
        CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, deadline.Token)

    shutdown.Cancel()
    let error = cancelledError linked.Token

    Expect.isTrue
        (RuntimeAuditCancellation.isOrderlyStop true false linked.Token error)
        "Actual orderly-stop cancellation is normal"

    deadline.Cancel()

    Expect.isFalse
        (RuntimeAuditCancellation.isOrderlyStop true true linked.Token error)
        "Overlapping deadline and shutdown cannot suppress quarantine"

    Expect.isFalse
        (RuntimeAuditCancellation.isOrderlyStop false true linked.Token error)
        "Deadline-only cancellation is a failure"

    Expect.isFalse
        (RuntimeAuditCancellation.isOrderlyStop true false shutdown.Token error)
        "An unrelated cancellation token is not normal stop"

    Expect.isFalse
        (RuntimeAuditCancellation.isOrderlyStop
            true
            false
            linked.Token
            (InvalidOperationException("Synthetic divergence")))
        "Integrity failure survives simultaneous shutdown"

let tests =
    testList
        "audit cancellation causes"
        [
            testCase
                "[CC-AUDIT-001] orderly stop remains distinct from deadline and simultaneous integrity failure"
                distinguishCauses
        ]
