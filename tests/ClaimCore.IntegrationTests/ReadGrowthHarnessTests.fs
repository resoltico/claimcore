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
            let! report = GrowthReadWorkloads.profile 1000 |> Async.AwaitTask

            Expect.equal
                (report.workloads |> Array.map _.name |> Set.ofArray)
                (set [ "search"; "filtered"; "notified"; "summary" ])
                "Every declared case read workload executes"

            for workload in report.workloads do
                Expect.equal workload.cases 1000 "The measured population is exact"
                Expect.equal workload.requests 100 "Every latency sample is observed"

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
