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

let private measure cases (name, order, expectedItems, fingerprint) read =
    task {
        for _ in 1 .. GrowthWorkloadPlan.warmupRequests do
            let! found = read ()
            Expect.equal found expectedItems "Warmup observes the independent seeded population"

        let durations = ResizeArray<double>()
        let mutable returned = 0
        let elapsed = Stopwatch.StartNew()

        for _ in 1 .. GrowthWorkloadPlan.measuredRequests do
            let watch = Stopwatch.StartNew()
            let! found = read ()
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
                order = order
                limit = GrowthWorkloadPlan.readLimit
                expectedItems = expectedItems
                returnedItems = returned
                seconds = elapsed.Elapsed.TotalSeconds
                p50Milliseconds = latencies[latencies.Length / 2]
                p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
                p99Milliseconds = latencies[int (float (latencies.Length - 1) * 0.99)]
                maximumMilliseconds = Array.last latencies
                workloadFingerprint = fingerprint
            |}
    }

let private authorityProfiles (database: PostgresFixture.Database) actor cases =
    task {
        for index in 0 .. GrowthWorkloadPlan.authorityEntries - 1 do
            let! reply =
                grant
                    actor
                    (id (GrowthWorkloadPlan.authorityOperationStart + index))
                    database.Fixture.Owner
                    {
                        Role = GrowthWorkloadPlan.authorityRole
                        Scope = GrantTarget.Installation
                    }
                    (index % 2 = 0)

            acceptedAuthority reply

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! counts =
            ClaimCore.Postgres.DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.ledger WHERE subject_kind='AUTHORITY'"
                [||]
                token

        Expect.equal
            (ClaimCore.Postgres.DatabaseSession.value<int64> 0 counts.Head)
            (int64 (GrowthWorkloadPlan.authorityEntries + 3))
            "History qualification contains the declared actual authority entry population"

        let reports = ResizeArray<_>()

        for name, target in
            [
                "authority-history", None
                "authority-history-actor", Some GrowthWorkloadPlan.authorityOwner
            ] do
            let query =
                {
                    TargetActorId = target |> Option.map ActorId.value
                    AfterCursor = None
                    Limit = GrowthWorkloadPlan.readLimit
                }

            let read () =
                task {
                    let! result = Service.authorityHistory actor query token

                    match result with
                    | Outcome.Succeeded page -> return page.Entries.Length
                    | _ -> return failtestf "Authority history growth failed: %A" result
                }

            let! report =
                measure
                    cases
                    (name,
                     "SEQUENCE",
                     GrowthWorkloadPlan.readLimit,
                     GrowthWorkloadPlan.authorityFingerprint cases target)
                    read

            reports.Add report

        return reports.ToArray()
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

            for name, filter, expectedItems in workloads do
                let! report =
                    measure
                        cases
                        (name,
                         CaseListOrder.token filter.Order,
                         expectedItems,
                         GrowthWorkloadPlan.readFingerprint cases filter)
                        (fun () -> read actor filter)

                reports.Add report

            // Seed authority events only after the existing query measurements; their declared
            // inputs and fingerprints remain unchanged for historical comparisons.
            if cases = 100000 then
                let! history = authorityProfiles database actor cases
                reports.AddRange history

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
