module internal ClaimCore.IntegrationTests.ReadProofMeasurements

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Postgres

/// Counts actual driver execution spans, never equating them with protocol round trips.
let measure label count (action: unit -> Task<'result>) =
    task {
        let mutable commands = 0
        use trace = new Activity("synthetic-proof-measurement")
        trace.Start() |> ignore
        use listener = new ActivityListener()

        listener.ShouldListenTo <-
            Func<ActivitySource, bool>(fun source ->
                source.Name.StartsWith("Npgsql", StringComparison.Ordinal))

        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)

        listener.ActivityStopped <-
            Action<Activity>(fun activity ->
                if activity.Kind = ActivityKind.Client && activity.TraceId = trace.TraceId then
                    Interlocked.Increment(&commands) |> ignore)

        ActivitySource.AddActivityListener listener
        let allocated = GC.GetTotalAllocatedBytes(true)
        let started = Stopwatch.StartNew()
        let! result = action ()

        printfn
            "PROOF_MEASUREMENT kind=%s subjects=%d commands=%d elapsedMs=%.3f allocationBytes=%d"
            label
            count
            commands
            started.Elapsed.TotalMilliseconds
            (GC.GetTotalAllocatedBytes(true) - allocated)

        Expect.isGreaterThan commands 0 "Instrumentation observes actual database execution."
        return result, commands
    }

let withFence (connectionString: string) (witness: WitnessProtocol) action =
    task {
        use source = NpgsqlDataSource.Create(connectionString)
        use! connection = source.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        use primary =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE",
                connection,
                transaction
            )

        let! _ = primary.ExecuteScalarAsync()
        let! state = witness.Snapshot(CancellationToken.None)

        use! _fence =
            witness.AcquireReadFence(state.WriterGeneration, CancellationToken.None)

        return! action connection transaction
    }
