module ClaimCore.IntegrationTests.ReadGrowthHarnessTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCaseAsync
        "worklist read growth harness measures actual population and pages"
        (async {
            let published = ResizeArray<_>()
            let phases = ResizeArray<GrowthReadWorkloads.ReadProgress * int>()

            let! report =
                GrowthReadWorkloads.profileWithProgress
                    1000
                    (fun progress -> phases.Add((progress, published.Count)))
                    (fun _ workload -> published.Add workload)
                |> Async.AwaitTask

            Expect.equal
                (published |> Seq.map _.name |> Seq.toArray)
                (report.workloads |> Array.map _.name)
                "Each completed profile is published once in the existing report order"

            for phase, expected in [ "work-seed", 4; "reminder-pair-seed", 6 ] do
                let observed =
                    phases
                    |> Seq.tryFind (fun (progress, _) -> progress.Phase = phase)
                    |> Option.map snd

                Expect.equal
                    observed
                    (Some expected)
                    "Earlier read reports are available before the next feature seed starts"

            Expect.isTrue
                (phases
                 |> Seq.forall (fun (progress, _) ->
                     progress.Completed >= 0 && progress.Completed <= progress.Expected))
                "Read progress reports only completed setup or measurement work"

            Expect.equal
                (report.workloads |> Array.map _.name |> Set.ofArray)
                (set
                    [
                        "search"
                        "filtered"
                        "notified"
                        "summary"
                        "reminder-derived-follow-up"
                        "reminder-derived-work-summary"
                        "reminder-pending"
                        "reminder-completed"
                        "reminder-current-case"
                        "reminder-financial-isolation"
                        "reminder-work-overview"
                    ])
                "Every declared case read workload executes"

            for workload in report.workloads do
                Expect.equal workload.cases 1000 "The measured population is exact"
                Expect.equal workload.requests 100 "Every latency sample is observed"
                Expect.equal workload.warmupRequests 5 "The existing warmup count is unchanged"

                let samples = workload.latencySamples

                Expect.equal
                    samples.milliseconds.Length
                    100
                    "Raw evidence contains every measured read"

                Expect.equal samples.order "observation" "Raw samples retain observation order"
                Expect.isFalse samples.warmupsIncluded "Raw read evidence excludes warmup requests"

                Expect.equal
                    samples.percentileIndex
                    "floor((sampleCount - 1) * percentile)"
                    "The existing percentile definition is recorded"

                let ordered = samples.milliseconds |> Array.sort

                Expect.equal
                    ordered[94]
                    workload.p95Milliseconds
                    "The p95 is the existing 95th sorted sample"

                Expect.equal
                    ordered[98]
                    workload.p99Milliseconds
                    "The p99 is the second largest of 100 samples"

                Expect.equal
                    ordered[99]
                    workload.maximumMilliseconds
                    "Raw evidence preserves the maximum"

                Expect.isTrue
                    (workload.p50Milliseconds <= workload.p95Milliseconds
                     && workload.p95Milliseconds <= workload.p99Milliseconds)
                    "Reported percentiles preserve sample ordering"

            match
                Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY"
                |> Option.ofObj
            with
            | Some directory ->
                Directory.CreateDirectory directory |> ignore

                File.WriteAllBytes(
                    Path.Combine(directory, "read-growth-smoke.json"),
                    JsonSerializer.SerializeToUtf8Bytes report
                )
            | None -> ()
        })
