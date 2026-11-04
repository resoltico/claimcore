module internal ClaimCore.IntegrationTests.GrowthReadWorkloads

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

type ReadProgress =
    {
        Cases: int
        Phase: string
        Completed: int
        Expected: int
        ObservedAt: DateTimeOffset
    }

let private progress progressed cases phase completed expected =
    progressed
        {
            Cases = cases
            Phase = phase
            Completed = completed
            Expected = expected
            ObservedAt = DateTimeOffset.UtcNow
        }

let private seed progressed actor count =
    task {
        progress progressed count "case-seed" 0 count

        for index in 0 .. count - 1 do
            let reference = GrowthWorkloadPlan.readReference index
            let registration = GrowthWorkloadPlan.readRegistration count index

            let! opened =
                Service.execute
                    actor
                    (ClaimCore.TestSupport.Inputs.caseOperation (
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
                        (ClaimCore.TestSupport.Inputs.caseOperation (
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
                        (ClaimCore.TestSupport.Inputs.caseOperation (
                            request
                                (1000002 + index * 3)
                                reference
                                revision
                                (AuthoredCaseAct.Bare BareAct.Close)
                        ))
                        token

                accepted closed |> ignore

            if (index + 1) % 10000 = 0 || index + 1 = count then
                progress progressed count "case-seed" (index + 1) count
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

let private measure
    progressed
    publish
    cases
    (name, order, expectedItems, fingerprint)
    (limit: Nullable<int>)
    read
    =
    task {
        progress progressed cases ("measure/" + name) 0 GrowthWorkloadPlan.measuredRequests

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

        // Keep the original aggregate construction, including the seconds boundary after sorting.
        let report =
            {|
                name = name
                cases = cases
                requests = latencies.Length
                warmupRequests = GrowthWorkloadPlan.warmupRequests
                order = order
                limit = limit
                expectedItems = expectedItems
                returnedItems = returned
                seconds = elapsed.Elapsed.TotalSeconds
                p50Milliseconds = latencies[latencies.Length / 2]
                p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
                p99Milliseconds = latencies[int (float (latencies.Length - 1) * 0.99)]
                maximumMilliseconds = Array.last latencies
                workloadFingerprint = fingerprint
            |}

        elapsed.Stop()

        let observed =
            {| report with
                latencySamples =
                    {|
                        milliseconds = durations.ToArray()
                        order = "observation"
                        warmupsIncluded = false
                        percentileIndex = "floor((sampleCount - 1) * percentile)"
                    |}
            |}

        // Export and progress callbacks run only after every existing metric has been frozen.
        publish observed
        progress progressed cases ("measure/" + name) observed.requests observed.requests
        return observed
    }

let private authorityProfiles progressed publish (database: PostgresFixture.Database) actor cases =
    task {
        progress progressed cases "authority-seed" 0 GrowthWorkloadPlan.authorityEntries

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

            if (index + 1) % 10000 = 0 || index + 1 = GrowthWorkloadPlan.authorityEntries then
                progress
                    progressed
                    cases
                    "authority-seed"
                    (index + 1)
                    GrowthWorkloadPlan.authorityEntries

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
                    progressed
                    publish
                    cases
                    (name,
                     "SEQUENCE",
                     GrowthWorkloadPlan.readLimit,
                     GrowthWorkloadPlan.authorityFingerprint cases target)
                    (Nullable GrowthWorkloadPlan.readLimit)
                    read

            reports.Add report

        return reports.ToArray()
    }

let private readSummary actor (cases: int) =
    task {
        let! result = Service.caseSummary actor CaseListFilter.empty<string> token

        match result with
        | Outcome.Succeeded summary ->
            Expect.equal summary.Counts.Total (int64 cases) "Summary counts the actual seeded cases"

            let totals (values: CurrencyTotal list) =
                values
                |> List.map (fun value ->
                    Currency.format value.Currency, DecimalTotal.format value.Amount)

            Expect.equal
                (totals summary.Claimed)
                [ "EUR", string (bigint cases * 1000I) ]
                "Every claimed amount contributes exactly once"

            Expect.equal
                (totals summary.Payable)
                [ "EUR", string (bigint ((cases + 4) / 5) * 750I) ]
                "Only the independently seeded decisions contribute payable amounts"

            return int summary.Counts.Total
        | _ -> return failtestf "Summary growth workload failed: %A" result
    }

// These new-feature measurements follow every common query, keeping its population unchanged.
let private workProfiles progressed publish (actor: ServiceContext) cases =
    task {
        progress progressed cases "work-seed" 0 (cases / 1000)
        let! context = Service.context actor () token

        let context =
            match context with
            | Outcome.Succeeded {
                                    Requester = RequesterState.Active value
                                } -> value
            | _ -> failtest "Case-work growth requires the actual registered handler context"

        let followUp = CalendarDate.ofDate (context.Runtime.Basis.BusinessDate.AddDays -1)

        let references = ResizeArray<string>()

        for index in 1..1000 .. cases - 1 do
            let reference = GrowthWorkloadPlan.readReference index

            let! changed =
                Service.execute
                    actor
                    (OperationInput.Work
                        {
                            OperationId = id (3000000 + index * 2)
                            Target = CaseSelector.Reference reference
                            ExpectedRevision = 0L
                            Act =
                                WorkAct.UpdateWork
                                    {
                                        Responsible = WorkValue.Set(ActorId.format context.ActorId)
                                        Summary = WorkValue.Keep
                                    }
                        })
                    token

            accepted changed |> ignore

            let! planned =
                Service.execute
                    actor
                    (OperationInput.Reminder
                        {
                            OperationId = id (3000001 + index * 2)
                            Target = CaseSelector.Reference reference
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Growth planned action"
                                        Due = CalendarDate.format followUp
                                    }
                        })
                    token

            accepted planned |> ignore
            references.Add reference

            if references.Count % 100 = 0 || references.Count = cases / 1000 then
                progress progressed cases "work-seed" references.Count (cases / 1000)

        // Independent population/count and row-content witnesses, rather than an empty Mine query.
        Expect.equal references.Count (cases / 1000) "One open case per thousand has explicit work"

        let expected =
            [
                for index in 0 .. cases - 1 do
                    if index % 1000 = 1 then
                        yield sprintf "READ-GROWTH-%07d" index
            ]
            |> List.truncate 50

        let filter =
            { CaseListFilter.empty<string> with
                Assignment = Some CaseAssignment.Mine
                Due = Some CaseDue.Overdue
                Order = CaseListOrder.FollowUp
            }

        let read () =
            task {
                let! result =
                    Service.list
                        actor
                        {
                            Page = { AfterCursor = None; Limit = 50 }
                            Filter = filter
                        }
                        token

                match result with
                | Outcome.Succeeded page ->
                    Expect.equal
                        (page.Items |> List.map (fun row -> CaseReference.format row.Reference))
                        expected
                        "Follow-up order returns the independently named actual assigned cases"

                    for row in page.Items do
                        match row.State with
                        | CaseState.Active _ ->
                            Expect.equal
                                (row.NextAction |> Option.map _.Due)
                                (Some followUp)
                                "The planned date survives actual PostgreSQL reads"

                            Expect.equal
                                (row.NextAction |> Option.map _.DueState)
                                (Some CaseDue.Overdue)
                                "The database observation classifies actual past follow-up"

                            Expect.equal
                                (row.Responsible |> Option.map _.ActorId)
                                (Some context.ActorId)
                                "Mine is explicit responsibility, not grant-based visibility"
                        | _ -> failtest "Only seeded active work can match the work filter"

                    return page.Items.Length
                | _ -> return failtest "Actual work-list growth query must succeed"
            }

        let summary () =
            task {
                let! result = Service.caseSummary actor filter token

                match result with
                | Outcome.Succeeded value ->
                    Expect.equal
                        value.Counts.Total
                        (int64 (cases / 1000))
                        "Filtered summary counts the independently expected assigned population"

                    let overdue =
                        value.Counts.Due |> List.find (fun count -> count.Value = CaseDue.Overdue)

                    Expect.equal
                        overdue.Count
                        value.Counts.Total
                        "Every matched work case is actually overdue"

                    return int value.Counts.Total
                | _ -> return failtest "Actual filtered work summary must succeed"
            }

        let! listing =
            measure
                progressed
                publish
                cases
                ("reminder-derived-follow-up",
                 "FOLLOW_UP",
                 min 50 (cases / 1000),
                 GrowthWorkloadPlan.workReadFingerprint cases filter false)
                (Nullable 50)
                read

        let! totals =
            measure
                progressed
                publish
                cases
                ("reminder-derived-work-summary",
                 "GROUPED",
                 cases / 1000,
                 GrowthWorkloadPlan.workReadFingerprint cases filter true)
                (Nullable())
                summary

        return [| listing; totals |]
    }

// Large child populations are seeded after common-case measurements, so feature cost cannot
// change the unchanged-read comparison inputs. Each population is checked independently.
let private reminderProfiles progressed publish actor cases =
    task {
        progress progressed cases "reminder-pair-seed" 0 (cases / 10)
        let! contextResult = Service.context actor () token

        let context =
            match contextResult with
            | Outcome.Succeeded {
                                    Requester = RequesterState.Active value
                                } -> value
            | other -> failtestf "Reminder growth context unavailable: %A" other

        let reference = GrowthWorkloadPlan.readReference 3
        let population = cases / 10

        let due =
            context.Runtime.Basis.BusinessDate
                .AddDays(-2)
                .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

        let target = CaseSelector.Reference reference
        let pendingIds = ResizeArray<string>()
        let completedIds = ResizeArray<string>()

        for index in 0 .. population - 1 do
            let pendingOperation = id (4000000 + index * 3)
            let terminalOperation = id (4000001 + index * 3)

            for operation in [ pendingOperation; terminalOperation ] do
                let! reply =
                    Service.execute
                        actor
                        (OperationInput.Reminder
                            {
                                OperationId = operation
                                Target = target
                                Act =
                                    AuthoredReminderAct.Create
                                        {
                                            Action = "High fanout action"
                                            Due = due
                                        }
                            })
                        token

                let receipt = accepted reply

                match receipt.Subject with
                | ReceiptSubject.Reminder {
                                              Effects = ReminderReceiptEffects.Single version
                                          } ->
                    Expect.equal
                        (ReminderId.value version.ReminderId)
                        operation
                        "Every creation uses the operation ID"
                | other -> failtestf "Creation must have one correlated reminder effect: %A" other

            let! completed =
                Service.execute
                    actor
                    (OperationInput.Reminder
                        {
                            OperationId = id (4000002 + index * 3)
                            Target = target
                            Act = AuthoredReminderAct.Complete(terminalOperation, 1L)
                        })
                    token

            accepted completed |> ignore
            pendingIds.Add(pendingOperation.ToString("D"))
            completedIds.Add(terminalOperation.ToString("D"))

            if (index + 1) % 1000 = 0 || index + 1 = population then
                progress progressed cases "reminder-pair-seed" (index + 1) population

        let request state cursor : ReminderListRequest =
            {
                Target = Some target
                State = Some state
                Due = None
                Assignment = None
                Text = Some "High fanout action"
                AfterCursor = cursor
                Limit = 50
                View = None
                IncludeInactive = None
            }

        let assertPage state expected =
            task {
                let! result = Service.reminders actor (request state None) token

                match result with
                | Outcome.Succeeded page ->
                    let actual =
                        page.Items
                        |> List.map (fun item -> Reminder.id item.Reminder |> ReminderId.format)

                    Expect.equal
                        actual
                        (expected |> List.sort |> List.truncate 50)
                        "Stable due/identity first-page order"

                    for row in page.Items do
                        Expect.equal (Reminder.state row.Reminder) state "Explicit state filter"

                        Expect.equal
                            (ReminderAction.format (Reminder.action row.Reminder))
                            "High fanout action"
                            "Bounded current action"

                        Expect.equal
                            (CalendarDate.format (Reminder.due row.Reminder))
                            due
                            "Retained planned date"

                    return page.Items.Length
                | other -> return failtestf "Actual reminder page failed: %A" other
            }
        // Walk each actual continuation once before latency sampling. Missing/duplicate rows fail.
        for state, expected in
            [
                ReminderState.Pending, List.ofSeq pendingIds
                ReminderState.Completed, List.ofSeq completedIds
            ] do
            let found = ResizeArray<string>()
            let mutable cursor = None
            let mutable more = true

            while more do
                let! result = Service.reminders actor (request state cursor) token

                match result with
                | Outcome.Succeeded page ->
                    for row in page.Items do
                        found.Add(Reminder.id row.Reminder |> ReminderId.format)

                    Expect.isLessThanOrEqual
                        found.Count
                        population
                        "Continuation must make bounded progress"

                    if page.NextCursor = cursor && page.NextCursor.IsSome then
                        failtest "Continuation repeated without progress"

                    cursor <- page.NextCursor
                    more <- cursor.IsSome
                | other -> failtestf "Continuation query failed: %A" other

            Expect.equal
                (List.ofSeq found)
                (List.sort expected)
                "Exact independent population, no omissions or duplicates"

        let fingerprint name =
            GrowthWorkloadPlan.reminderReadFingerprint cases population name

        let! pending =
            measure
                progressed
                publish
                cases
                ("reminder-pending", "DUE_ID", min 50 population, fingerprint "pending")
                (Nullable 50)
                (fun () -> assertPage ReminderState.Pending (List.ofSeq pendingIds))

        let! completed =
            measure
                progressed
                publish
                cases
                ("reminder-completed", "DUE_ID", min 50 population, fingerprint "completed")
                (Nullable 50)
                (fun () -> assertPage ReminderState.Completed (List.ofSeq completedIds))

        let current () =
            task {
                let! result = Service.get actor { Target = target } token

                match result with
                | Outcome.Succeeded view ->
                    Expect.equal
                        (Revision.value view.Snapshot.Revision)
                        1L
                        "Child activity does not advance financial facts"

                    Expect.equal
                        view.Work.PendingReminders
                        (int64 population)
                        "Pending count ignores completed rows"

                    Expect.equal
                        view.Work.OverdueReminders
                        (int64 population)
                        "Past-due reminders remain pending"

                    Expect.equal
                        (view.Work.FollowUp |> Option.map CalendarDate.format)
                        (Some due)
                        "Minimum pending date"

                    Expect.isFalse view.Work.NeedsNextAction "Pending work supplies coverage"
                    return 1
                | other -> return failtestf "Bounded current case query failed: %A" other
            }

        let! currentReport =
            measure
                progressed
                publish
                cases
                ("reminder-current-case", "CASE_ID", 1, fingerprint "current")
                (Nullable 1)
                current

        let financial () =
            task {
                let filter =
                    { CaseListFilter.empty<string> with
                        Text = Some reference
                    }

                let! result = Service.caseSummary actor filter token

                match result with
                | Outcome.Succeeded summary ->
                    Expect.equal summary.Counts.Total 1L "Child fanout selects exactly one case"

                    Expect.equal
                        (summary.Claimed
                         |> List.map (fun total ->
                             Currency.format total.Currency, DecimalTotal.format total.Amount))
                        [ ("EUR", "1000") ]
                        "Money is counted once despite high pending and completed fanout"

                    Expect.isEmpty summary.Payable "Seeded case 3 has no decision"
                    return 1
                | other -> return failtestf "Fanout financial query failed: %A" other
            }

        let! financialReport =
            measure
                progressed
                publish
                cases
                ("reminder-financial-isolation", "GROUPED", 1, fingerprint "financial")
                (Nullable())
                financial

        let overview () =
            task {
                let! result = Service.workOverview actor { Mine = false } token

                match result with
                | Outcome.Succeeded overview ->
                    Expect.equal
                        overview.ActionableCases
                        (int64 (cases / 2))
                        "Only active open cases"

                    Expect.equal
                        overview.MissingPlanCases
                        (int64 (cases / 2 - cases / 1000 - 1))
                        "Each covered case counted once"

                    Expect.equal overview.OverdueCases (int64 (cases / 1000 + 1)) "Case units"

                    Expect.equal
                        overview.OverdueReminders
                        (int64 (cases / 1000 + population))
                        "Reminder units"

                    Expect.equal
                        overview.UnassignedCases
                        (int64 (cases / 2 - cases / 1000))
                        "Inherited assignment"

                    Expect.equal overview.UnavailableAssigneeCases 0L "Enabled seeded assignee"

                    Expect.equal
                        overview.AttentionCases
                        (int64 (cases / 2))
                        "Union of overlapping attention conditions"

                    return int overview.ActionableCases
                | other -> return failtestf "Accessible-scope overview failed: %A" other
            }

        let! overviewReport =
            measure
                progressed
                publish
                cases
                ("reminder-work-overview", "ACCESSIBLE", cases / 2, fingerprint "overview")
                (Nullable())
                overview

        return [| pending; completed; currentReport; financialReport; overviewReport |]
    }

let profileWithProgress cases progressed completed =
    task {
        let! database = PostgresFixture.create ()

        try
            let environment = GrowthEnvironment.source ()
            let publish = completed environment
            let! actor = caseWorker database.Fixture
            do! seed progressed actor cases
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
                    "notified",
                    { CaseListFilter.empty<string> with
                        Order = CaseListOrder.Notified
                    },
                    min 50 cases
                ]

            let reports = ResizeArray<_>()

            for name, filter, expectedItems in workloads do
                let! report =
                    measure
                        progressed
                        publish
                        cases
                        (name,
                         CaseListOrder.token filter.Order,
                         expectedItems,
                         GrowthWorkloadPlan.readFingerprint cases filter)
                        (Nullable GrowthWorkloadPlan.readLimit)
                        (fun () -> read actor filter)

                reports.Add report

            let! summary =
                measure
                    progressed
                    publish
                    cases
                    ("summary", "GROUPED", cases, GrowthWorkloadPlan.summaryFingerprint cases)
                    (Nullable())
                    (fun () -> readSummary actor cases)

            reports.Add summary

            // Seed authority events only after the existing query measurements; their declared
            // inputs and fingerprints remain unchanged for historical comparisons.
            if cases = 100000 then
                let! history = authorityProfiles progressed publish database actor cases
                reports.AddRange history

            let! work = workProfiles progressed publish actor cases
            reports.AddRange work

            let! reminders = reminderProfiles progressed publish actor cases
            reports.AddRange reminders

            return
                {|
                    environment = environment
                    database = population
                    workloads = reports.ToArray()
                |}
        finally
            database.Fixture.Dispose()
    }

let profile cases =
    profileWithProgress cases ignore (fun _ _ -> ())

let run progressed completed populationCompleted =
    async {
        for cases in GrowthWorkloadPlan.readPopulations do
            let! _ = profileWithProgress cases progressed completed |> Async.AwaitTask
            // Aggregate bookkeeping stays after population work and fixture disposal.
            populationCompleted ()
    }
