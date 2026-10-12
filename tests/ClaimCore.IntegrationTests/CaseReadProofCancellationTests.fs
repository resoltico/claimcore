module ClaimCore.IntegrationTests.CaseReadProofCancellationTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ReadProofMeasurements

let private subjects = lazy (CaseReadProofBatchTests.receipts 9 |> List.map _.Case)

let private listen (trace: Activity) (caller: CancellationTokenSource) onStart boundary =
    let mutable commands = 0
    let listener = new ActivityListener()

    listener.ShouldListenTo <-
        Func<ActivitySource, bool>(fun source ->
            source.Name.StartsWith("Npgsql", StringComparison.Ordinal))

    listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)

    let relevant (activity: Activity) =
        activity.Kind = ActivityKind.Client && activity.TraceId = trace.TraceId

    listener.ActivityStarted <-
        Action<Activity>(fun activity ->
            if relevant activity then
                let count = Interlocked.Increment(&commands)

                if onStart && count = boundary then
                    caller.Cancel())

    listener.ActivityStopped <-
        Action<Activity>(fun activity ->
            if relevant activity && not onStart && Volatile.Read(&commands) = boundary then
                caller.Cancel())

    ActivitySource.AddActivityListener listener

    listener

let private cancellation onStart boundary =
    let label =
        if onStart then
            "acquisition start"
        else
            "acquisition completion"

    testCase
        ($"[CC-AUTH-001] page cancellation at {label} {boundary} releases its proof fence")
        (fun () ->
            let claims = subjects.Value
            let witness = witnessProtocol ()

            withFence (appConnection ()) witness (fun connection transaction ->
                task {
                    use caller = new CancellationTokenSource()
                    use trace = new Activity("synthetic-cancelled-proof")
                    trace.Start() |> ignore
                    use _listener = listen trace caller onStart boundary

                    if boundary = 0 then
                        caller.Cancel()

                    let mutable cancelled = false

                    try
                        let! _ =
                            CaseReadEvidence.page
                                witness
                                connection
                                transaction
                                claims
                                caller.Token

                        ()
                    with :? OperationCanceledException when caller.IsCancellationRequested ->
                        cancelled <- true

                    Expect.isTrue
                        caller.IsCancellationRequested
                        "The selected real driver boundary was reached."

                    Expect.isTrue cancelled "Cancellation produces no partially proved page."
                })
            |> await

            // A fresh ordered lease and query also detect a stranded connector or witness fence.
            withFence (appConnection ()) witness (fun connection transaction ->
                CaseReadEvidence.page witness connection transaction claims CancellationToken.None)
            |> await
            |> fun result -> Expect.isNone result "Cancellation left the exact subjects readable.")

let tests =
    testList
        "read proof cancellation boundaries"
        ([ 0..18 ]
         |> List.map (cancellation true)
         |> fun tests -> tests @ ([ 5; 6; 11; 12; 17; 18 ] |> List.map (cancellation false)))
