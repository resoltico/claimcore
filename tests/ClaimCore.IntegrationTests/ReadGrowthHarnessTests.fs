module ClaimCore.IntegrationTests.ReadGrowthHarnessTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

[<Tests>]
let progressSerialization =
    testCase "read growth progress atomically preserves its serialized fields" (fun () ->
        let directory =
            Directory.CreateTempSubdirectory("claimcore-read-progress-").FullName

        try
            let observedAt = DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.Zero)

            let progress: GrowthReadWorkloads.ReadProgress =
                {
                    Cases = 1000
                    Phase = "reminder-pair-seed"
                    Completed = 17
                    Expected = 100
                    ObservedAt = observedAt
                }

            GrowthQualificationTests.writeReadProgress directory 6 35 None progress
            let path = Path.Combine(directory, "read-progress.json")
            use first = JsonDocument.Parse(File.ReadAllBytes path)
            let firstRoot = first.RootElement
            let firstProgress = firstRoot.GetProperty("progress")

            Expect.equal
                (firstRoot.GetProperty("kind").GetString())
                "read-progress"
                "Progress artifact kind"

            Expect.equal
                (firstRoot.GetProperty("completedReadProfiles").GetInt32())
                6
                "Completed profile count"

            Expect.equal
                (firstRoot.GetProperty("requiredReadProfiles").GetInt32())
                35
                "Required profile count"

            Expect.equal
                (firstRoot.GetProperty("lastCompletedReport").ValueKind)
                JsonValueKind.Null
                "No fabricated previous report"

            Expect.equal
                (firstProgress.GetProperty("cases").GetInt32())
                1000
                "Actual population survives serialization"

            Expect.equal
                (firstProgress.GetProperty("phase").GetString())
                "reminder-pair-seed"
                "Actual phase survives serialization"

            Expect.equal
                (firstProgress.GetProperty("completed").GetInt32())
                17
                "Partial setup count survives serialization"

            Expect.equal
                (firstProgress.GetProperty("expected").GetInt32())
                100
                "Expected setup count survives serialization"

            Expect.equal
                (firstProgress.GetProperty("observedAt").GetDateTimeOffset())
                observedAt
                "Observation instant survives serialization"

            Expect.isFalse
                (File.Exists(path + ".writing"))
                "Atomic publication consumes the staging file"

            GrowthQualificationTests.writeReadProgress
                directory
                7
                35
                (Some "read-workload-06.json")
                { progress with
                    Phase = "measure/reminder-pending"
                    Completed = 100
                }

            use second = JsonDocument.Parse(File.ReadAllBytes path)

            Expect.equal
                (second.RootElement.GetProperty("completedReadProfiles").GetInt32())
                7
                "Existing progress is replaced"

            Expect.equal
                (second.RootElement.GetProperty("lastCompletedReport").GetString())
                "read-workload-06.json"
                "Latest completed report is retained"

            Expect.equal
                (second.RootElement.GetProperty("progress").GetProperty("phase").GetString())
                "measure/reminder-pending"
                "New phase replaces old phase"

            Expect.equal
                (second.RootElement.GetProperty("progress").GetProperty("completed").GetInt32())
                100
                "Completed measurement replaces partial setup"

            Expect.isFalse (File.Exists(path + ".writing")) "Replacement consumes its staging file"
        finally
            Directory.Delete(directory, true))

[<Tests>]
let tests =
    testCaseAsync
        "worklist read growth harness measures actual population and pages"
        (async {
            let published = ResizeArray<_>()
            let phases = ResizeArray<GrowthReadWorkloads.ReadProgress * int>()

            let progressDirectory =
                Directory.CreateTempSubdirectory("claimcore-read-harness-").FullName

            use _cleanup =
                { new IDisposable with
                    member _.Dispose() =
                        Directory.Delete(progressDirectory, true)
                }

            let! report =
                GrowthReadWorkloads.profileWithProgress
                    1000
                    (fun progress ->
                        let lastCompletedReport =
                            if published.Count = 0 then
                                None
                            else
                                Some(sprintf "read-workload-%02d.json" (published.Count - 1))

                        GrowthQualificationTests.writeReadProgress
                            progressDirectory
                            published.Count
                            14
                            lastCompletedReport
                            progress

                        use emitted =
                            JsonDocument.Parse(
                                File.ReadAllBytes(
                                    Path.Combine(progressDirectory, "read-progress.json")
                                )
                            )

                        let serialized = emitted.RootElement.GetProperty("progress")

                        Expect.equal
                            (serialized.GetProperty("cases").GetInt32())
                            1000
                            "Runtime emitter records actual population"

                        Expect.equal
                            (serialized.GetProperty("phase").GetString())
                            progress.Phase
                            "Runtime callback publishes each actual phase"

                        Expect.equal
                            (serialized.GetProperty("completed").GetInt32())
                            progress.Completed
                            "Runtime callback preserves completed work"

                        Expect.equal
                            (serialized.GetProperty("expected").GetInt32())
                            progress.Expected
                            "Runtime callback preserves required work"

                        Expect.equal
                            (serialized.GetProperty("observedAt").GetDateTimeOffset())
                            progress.ObservedAt
                            "Runtime callback preserves observation time"

                        Expect.equal
                            (emitted.RootElement.GetProperty("completedReadProfiles").GetInt32())
                            published.Count
                            "Runtime publication count matches emitted workload order"

                        Expect.isFalse
                            (File.Exists(
                                Path.Combine(progressDirectory, "read-progress.json.writing")
                            ))
                            "Runtime progress is atomically published"

                        phases.Add((progress, published.Count)))
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
                        "core-case-views"
                        "core-reminder-views"
                        "core-case-timeline"
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
