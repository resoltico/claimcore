module ClaimCore.IntegrationTests.RuntimeHealthTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting

type private HealthClock() =
    inherit TimeProvider()
    let mutable timestamp = 0L
    override _.GetTimestamp() = timestamp
    override _.TimestampFrequency = TimeSpan.TicksPerSecond

    member _.Set(seconds: int64) =
        timestamp <- seconds * TimeSpan.TicksPerSecond

let private pending () =
    TaskCompletionSource<string * string * bool>(TaskCreationOptions.RunContinuationsAsynchronously)

let private unknown = "UNKNOWN", "QUARANTINED", false
let private green = "REAL_DATA", "ACTIVE", true

let private wait condition =
    Expect.isTrue
        (SpinWait.SpinUntil(Func<bool> condition, 2000))
        "Owned worker reaches its controlled boundary."

let private epoch () =
    use health = new RuntimeHealthObservation()
    let work = pending ()
    let mutable started = false

    health.Start(fun _ ->
        started <- true
        work.Task)

    try
        wait (fun () -> started)
        Expect.equal (health.Read()) unknown "Cold observation is conservative."
        health.Invalidate()
        work.SetResult green
        Thread.Sleep 30

        Expect.equal
            (health.Read())
            unknown
            "A sample from an older safety epoch cannot restore green."

        health.RequestStop()
        Expect.equal (health.Read()) unknown "Closing invalidates immediately."
    finally
        work.TrySetResult unknown |> ignore

let private freshness () =
    let clock = HealthClock()
    use health = new RuntimeHealthObservation(timeProvider = clock)
    health.Start(fun _ -> Task.FromResult green)
    wait (fun () -> health.Read() = green)
    clock.Set 15L
    Expect.equal (health.Read()) unknown "Exact monotonic freshness boundary fails closed."
    clock.Set -1L
    Expect.equal (health.Read()) unknown "Negative elapsed age cannot extend trust."

let private timeout () =
    use health = new RuntimeHealthObservation()
    let work = pending ()
    let mutable calls = 0
    let mutable cancelled = false

    health.Start(fun token ->
        Interlocked.Increment(&calls) |> ignore
        token.Register(fun () -> cancelled <- true) |> ignore
        work.Task)

    try
        wait (fun () -> calls = 1)

        Expect.isTrue
            (SpinWait.SpinUntil((fun () -> cancelled), 7000))
            "Sample budget cancels ignored work."

        Expect.equal calls 1 "Timed-out work remains owned; replacement cannot overlap."
        Expect.equal (health.Read()) unknown "Timeout is not ready."
        work.SetResult green
        Thread.Sleep 30

        Expect.equal
            (health.Read())
            unknown
            "Late success permanently loses publication permission."
    finally
        work.TrySetResult unknown |> ignore

let tests =
    testList
        "runtime health reporting"
        [
            testCase "[CC-RUN-001] health safety epoch fences late publication and closing" epoch
            testCase
                "[CC-RUN-001] health age uses monotonic sample start and exact expiry"
                freshness
            testCase "[CC-RUN-001] timed-out health work remains owned without overlap" timeout
        ]
