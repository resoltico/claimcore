module internal ClaimCore.IntegrationTests.GrowthReadWorkloads

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private seed actor count =
    task {
        for index in 0 .. count - 1 do
            let reference = GrowthWorkloadPlan.readReference index
            let registration = GrowthWorkloadPlan.readRegistration count index

            let! opened =
                Service.execute
                    actor
                    (OperationInput.Case(
                        request
                            (1000000 + index * 3)
                            reference
                            0L
                            (AuthoredCaseAct.Open registration)
                    ))
                    token

            accepted opened |> ignore
            let mutable revision = 1L

            if index % GrowthWorkloadPlan.decideEvery = 0 then
                let! decided =
                    Service.execute
                        actor
                        (OperationInput.Case(
                            request
                                (1000001 + index * 3)
                                reference
                                revision
                                (AuthoredCaseAct.Decide Inputs.decision)
                        ))
                        token

                accepted decided |> ignore
                revision <- revision + 1L

            if index % GrowthWorkloadPlan.closeEvery = 0 then
                let! closed =
                    Service.execute
                        actor
                        (OperationInput.Case(
                            request
                                (1000002 + index * 3)
                                reference
                                revision
                                (AuthoredCaseAct.Bare BareAct.Close)
                        ))
                        token

                accepted closed |> ignore
    }

let private read actor filter =
    task {
        let! result =
            Service.list
                actor
                {
                    Page =
                        {
                            AfterCursor = None
                            Limit = GrowthWorkloadPlan.readLimit
                        }
                    Filter = filter
                }
                token

        match result with
        | Outcome.Succeeded page -> return page.Items.Length
        | _ -> return failtestf "Read growth workload failed: %A" result
    }

let private measure actor cases (name, filter, expectedItems) =
    task {
        for _ in 1 .. GrowthWorkloadPlan.warmupRequests do
            let! found = read actor filter
            Expect.equal found expectedItems "Warmup observes the independent seeded population"

        let durations = ResizeArray<double>()
        let mutable returned = 0
        let elapsed = Stopwatch.StartNew()

        for _ in 1 .. GrowthWorkloadPlan.measuredRequests do
            let watch = Stopwatch.StartNew()
            let! found = read actor filter
            durations.Add watch.Elapsed.TotalMilliseconds

            Expect.equal
                found
                expectedItems
                "Every measured query returns the independently expected page size"

            returned <- returned + found

        let latencies = durations.ToArray() |> Array.sort

        return
            {|
                name = name
                cases = cases
                requests = latencies.Length
                warmupRequests = GrowthWorkloadPlan.warmupRequests
                order = CaseListOrder.token filter.Order
                limit = GrowthWorkloadPlan.readLimit
                expectedItems = expectedItems
                returnedItems = returned
                seconds = elapsed.Elapsed.TotalSeconds
                p50Milliseconds = latencies[latencies.Length / 2]
                p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
                p99Milliseconds = latencies[int (float (latencies.Length - 1) * 0.99)]
                maximumMilliseconds = Array.last latencies
                workloadFingerprint = GrowthWorkloadPlan.readFingerprint cases filter
            |}
    }

let profile cases =
    task {
        let! database = PostgresFixture.create ()

        try
            let environment = GrowthEnvironment.source ()
            let! actor = caseWorker database.Fixture
            do! seed actor cases
            let! population = GrowthEnvironment.database database

            Expect.equal
                population.cases
                (int64 cases)
                "Read qualification uses the declared actual case population"

            let workloads =
                [
                    "search",
                    CaseListFilter.create [] [] [] (Some "needle") CaseListOrder.Reference,
                    1
                    "filtered",
                    CaseListFilter.create
                        [ Lifecycle.Active ]
                        [ CaseStatus.Closed ]
                        [ CaseProgress.Decided ]
                        None
                        CaseListOrder.Recent,
                    min 50 ((cases + 9) / 10)
                ]

            let reports = ResizeArray<_>()

            for workload in workloads do
                let! report = measure actor cases workload
                reports.Add report

            return
                {|
                    environment = environment
                    database = population
                    workloads = reports.ToArray()
                |}
        finally
            database.Fixture.Dispose()
    }

let run completed =
    async {
        for cases in GrowthWorkloadPlan.readPopulations do
            let! report = profile cases |> Async.AwaitTask

            for workload in report.workloads do
                completed report.environment workload
    }
