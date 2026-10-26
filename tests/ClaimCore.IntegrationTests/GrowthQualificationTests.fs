module ClaimCore.IntegrationTests.GrowthQualificationTests

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

type private Workload =
    {
        Writes: int
        Cases: int
        Actors: int
        Writers: int
    }

let private subjects count =
    [
        for index in 1..count ->
            PrincipalKey.human "https://issuer.example/" ("synthetic-growth-" + string index)
            |> Result.defaultWith failtest
    ]

let private authority (actor: IActorClaimsCore) principals =
    task {
        for index, principal in principals |> List.indexed do
            let! registered =
                actor.Execute(
                    OperationInput.Authority(
                        authorityRequest
                            (id (100000 + index * 20))
                            principal
                            AdmittedAuthorityAct.Register
                    ),
                    token
                )

            acceptedAuthority registered


            let grants =
                [
                    yield Role.CaseHandler, GrantTarget.Installation
                    for target in 0..8 ->
                        Role.CaseReader,
                        GrantTarget.CaseReference("synthetic-growth-" + string target)
                    if index < 7 then
                        yield Role.Auditor, GrantTarget.Installation
                ]

            for offset, (role, scope) in grants |> List.indexed do
                let! granted =
                    grant actor (id (100001 + index * 20 + offset)) principal role scope true

                acceptedAuthority granted

    }

let private openCases (actor: IActorClaimsCore) count =
    task {
        for index in 0 .. count - 1 do
            let! result =
                actor.Execute(
                    OperationInput.Case(
                        request
                            (1000 + index)
                            ("synthetic-growth-" + string index)
                            0L
                            (CaseAct.Open registration)
                    ),
                    token
                )

            accepted result |> ignore

    }

let private act revision =
    let amended =
        { registration with
            ClaimantName = "Synthetic revision " + string revision
        }

    match (revision - 1L) % 8L with
    | 0L -> CaseAct.AmendRegistration amended
    | 1L ->
        CaseAct.Decide
            {
                PaymentDecisionDate = "2026-08-04"
                PayableAmount = "100.00"
                PayableCurrency = "EUR"
            }
    | 2L -> CaseAct.RecordPayment { PaymentDate = "2026-08-05" }
    | 3L -> CaseAct.ClearPayment
    | 4L ->
        CaseAct.Correct
            {
                Registration = RegistrationGroup.Replace amended
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            }
    | 5L -> CaseAct.WithdrawDecision
    | 6L -> CaseAct.Close
    | _ -> CaseAct.Reopen

let private writeLane (actor: IActorClaimsCore) workload lane =
    task {
        let durations = ResizeArray<float>()
        let revisions = Array.create workload.Cases 1L

        let targets =
            [| for index in lane .. workload.Writers .. workload.Cases - 1 -> index |]

        let mutable turn = 0

        for index in lane .. workload.Writers .. workload.Writes - 1 do
            let target = targets[turn % targets.Length]
            let revision = revisions[target]

            let authored =
                request
                    (200000 + index)
                    ("synthetic-growth-" + string target)
                    revision
                    (act revision)

            let watch = Stopwatch.StartNew()
            let! result = actor.Execute(OperationInput.Case authored, token)
            accepted result |> ignore

            durations.Add watch.Elapsed.TotalMilliseconds
            revisions[target] <- revision + 1L
            turn <- turn + 1

        return durations.ToArray()
    }

let private verifyMeasured (database: PostgresFixture.Database) =
    task {
        let baseline = GC.GetTotalMemory true
        use runner = Process.GetCurrentProcess()
        let mutable peakManaged = baseline
        let mutable peakWorking = runner.WorkingSet64
        let watch = Stopwatch.StartNew()
        let verification = Verification.verify database.OwnerSource None token

        while not verification.IsCompleted do
            runner.Refresh()
            peakManaged <- max peakManaged (GC.GetTotalMemory false)
            peakWorking <- max peakWorking runner.WorkingSet64
            do! Task.Delay 5

        let! result = verification

        return
            VerificationSupport.position result,
            watch.Elapsed.TotalMilliseconds,
            baseline,
            peakManaged,
            peakWorking
    }

let private timeout (database: PostgresFixture.Database) =
    task {
        use! holder = database.RuntimeSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()
        let! _ = Transactions.head holder token

        let store =
            LedgerStore(
                database.RuntimeSource,
                database.Fixture.InstallationId,
                { Transactions.timeouts with
                    LockMilliseconds = 100
                }
            )
            :> ILedgerStore

        let watch = Stopwatch.StartNew()

        let! result =
            store.WriteCase(
                database.Fixture.Owner,
                id 999999,
                "synthetic-growth-timeout",
                (fun _ -> LedgerDecision.Refuse Refusal.ResourceUnavailable),
                token
            )

        Expect.equal
            result
            (Error StoreFailure.Unavailable)
            "Lock timeout is a definite unavailable result"

        do! transaction.RollbackAsync()
        return watch.Elapsed.TotalMilliseconds
    }

let private report
    workload
    elapsed
    (latencies: float array)
    timeoutMilliseconds
    ((position: LedgerPosition), verificationMilliseconds, beforeMemory, peakManaged, peakWorking)
    =
    use runner = Process.GetCurrentProcess()

    {|
        writes = workload.Writes
        setupAcceptedOperations = 10 + workload.Cases + workload.Actors * 11
        qualificationAcceptedOperations = 1006
        cases = workload.Cases
        actors = workload.Actors + 1
        grants = workload.Actors * 10 + 10
        writers = workload.Writers
        ledgerEntries = position.Sequence
        seconds = elapsed
        writesPerSecond = float workload.Writes / elapsed
        p50Milliseconds = latencies[latencies.Length / 2]
        p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
        p99Milliseconds = latencies[int (float (latencies.Length - 1) * 0.99)]
        maximumMilliseconds = Array.last latencies
        operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        architecture = string System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        processors = Environment.ProcessorCount
        headWaitAndHoldMilliseconds = Nullable<float>()
        verificationMilliseconds = verificationMilliseconds
        managedBytesBeforeVerification = beforeMemory
        managedBytesAfterVerification = GC.GetTotalMemory false
        processPeakWorkingSetBytes =
            if runner.PeakWorkingSet64 > 0L then
                Nullable runner.PeakWorkingSet64
            else
                Nullable()
        sampledPeakManagedBytes = peakManaged
        sampledPeakWorkingSetBytes = peakWorking
        lockTimeoutMilliseconds = timeoutMilliseconds
        unexpectedErrors = 0
    |}

let private history workload (actor: IActorClaimsCore) =
    task {
        let! history =
            actor.History(
                {
                    CaseReference = "synthetic-growth-0"
                    AfterCursor = None
                    Limit = 50
                    Detail = HistoryDetail.Full
                },
                token
            )

        match history with
        | ReadReply.Succeeded page ->
            let operations = (workload.Writes + workload.Writers - 1) / workload.Writers
            let targets = (workload.Cases + workload.Writers - 1) / workload.Writers

            Expect.equal
                page.Entries.Length
                (min 50 (2 + (operations + targets - 1) / targets))
                "Bounded full history is complete"
        | _ -> failtest "Growth history read failed"

    }

let private verified workload actor database =
    task {
        let! verification = verifyMeasured database
        let position, _, _, _, _ = verification

        Expect.equal
            position.Sequence
            (int64 (1016 + workload.Cases + workload.Writes + workload.Actors * 11))
            "Every accepted write is counted"

        do! history workload actor

        return verification
    }

let private measuredWrites workload database actor protection =
    task {
        let watch = Stopwatch.StartNew()

        let writes =
            [| for lane in 0 .. workload.Writers - 1 -> writeLane actor workload lane |]

        let authorityWork = authority actor (subjects workload.Actors)
        let sessionWork = GrowthSessions.run database protection

        do!
            Task.WhenAll(
                Array.append
                    (writes |> Array.map (fun work -> work :> Task))
                    [| authorityWork :> Task; sessionWork :> Task |]
            )

        let lanes = writes |> Array.map _.Result
        let elapsed = watch.Elapsed.TotalSeconds
        let latencies = lanes |> Array.concat |> Array.sort

        return elapsed, latencies, sessionWork.Result
    }

let private measure keyRing workload (database: PostgresFixture.Database) =
    task {
        let sessionProtection = GrowthProtection.sessions keyRing

        let database =
            { database with
                Fixture =
                    { database.Fixture with
                        Store = GrowthProtection.store keyRing database
                    }
            }

        let environment = GrowthEnvironment.source ()
        let! databaseBefore = GrowthEnvironment.database database
        let! actor = caseWorker database.Fixture

        do! openCases actor workload.Cases

        let! elapsed, latencies, sessions =
            measuredWrites workload database actor sessionProtection


        let! timeoutMilliseconds = timeout database

        let! controls = GrowthControls.run database actor (subjects workload.Actors).Head
        let! verification = verified workload actor database

        let! databaseAfter = GrowthEnvironment.database database

        Expect.equal
            (databaseAfter.cases, databaseAfter.actors, databaseAfter.grants)
            (1000L, 100L, 1000L)
            "The final case, actor and grant populations match qualification inputs"

        Expect.equal
            databaseAfter.sessions
            0L
            "Owned operational qualification state was explicitly removed"

        return
            {|
                environment = environment
                protection = "ASP.NET Core DataProtection with an owned filesystem key ring"
                databaseBefore = databaseBefore
                databaseAfter = databaseAfter
                business = report workload elapsed latencies timeoutMilliseconds verification
                sessions = sessions
                controls = controls
            |}
    }

let private cardinalityWrites cases keyRing (database: PostgresFixture.Database) =
    task {
        let database =
            { database with
                Fixture =
                    { database.Fixture with
                        Store = GrowthProtection.store keyRing database
                    }
            }

        let! actor = caseWorker database.Fixture

        do! openCases actor cases

        let! _ =
            writeLane
                actor
                {
                    Writes = 1000 - cases
                    Cases = cases
                    Actors = 0
                    Writers = 1
                }
                0

        let! position, elapsed, baseline, peakManaged, peakWorking = verifyMeasured database
        Expect.equal position.Sequence 1003L "Resource comparisons keep accepted history fixed"
        let! population = GrowthEnvironment.database database
        Expect.equal population.cases (int64 cases) "Compared resource population is exact"

        return
            {|
                cases = cases
                acceptedEntries = position.Sequence
                verificationMilliseconds = elapsed
                managedBytesBeforeVerification = baseline
                managedBytesAfterVerification = GC.GetTotalMemory false
                sampledPeakManagedBytes = peakManaged
                sampledPeakWorkingSetBytes = peakWorking
                database = population
            |}
    }

let private cardinalityProfile cases =
    task {
        let! database = PostgresFixture.create ()

        let! outcome =
            task {
                try
                    let! result =
                        GrowthProtection.withKeyRing (fun keyRing ->
                            cardinalityWrites cases keyRing database)

                    return Ok result
                with error ->
                    return Error error
            }

        match outcome with
        | Ok result ->
            database.Fixture.Dispose()
            return result
        | Error error ->
            ClaimCore.TestSupport.FixtureDisposal.afterFailure error [ database.Fixture.Dispose ]
            return raise error
    }

let private cardinality () =
    task {
        let reports = ResizeArray<_>()

        for cases in [ 1; 100; 1000 ] do
            let! report = cardinalityProfile cases
            reports.Add report

        return
            {|
                environment = GrowthEnvironment.source ()
                fixedCaseEntries = 1000
                fixedAuthorityEntries = 3
                profiles = reports.ToArray()
            |}
    }

let private profiles () =
    let required =
        [
            for writes in [ 1000; 10000; 100000 ] do
                for writers in [ 1; 4; 16 ] -> writes, writers
        ]

    match Environment.GetEnvironmentVariable("CLAIMCORE_GROWTH_PROFILE") |> Option.ofObj with
    | None -> required
    | Some selected ->
        match
            required
            |> List.tryFind (fun (writes, writers) ->
                selected = string writes + ":" + string writers)
        with
        | Some profile -> [ profile ]
        | None -> failtest "CLAIMCORE_GROWTH_PROFILE must name a required writes:writers profile."

let private workloads selected completed =
    async {
        for writes, writers in selected do
            let cases, actors = 1000, 99

            let! database = PostgresFixture.create () |> Async.AwaitTask

            let mutable failure: exn option = None

            try
                try
                    let! report =
                        GrowthProtection.withKeyRing (fun keyRing ->
                            measure
                                keyRing
                                {
                                    Writes = writes
                                    Cases = cases
                                    Actors = actors
                                    Writers = writers
                                }
                                database)
                        |> Async.AwaitTask

                    completed report
                with error ->
                    failure <- Some error
            finally
                match failure with
                | None -> database.Fixture.Dispose()
                | Some error ->
                    ClaimCore.TestSupport.FixtureDisposal.afterFailure
                        error
                        [ database.Fixture.Dispose ]

            Expect.notEqual
                (string database.Container.State)
                "Running"
                "Owned growth container is stopped"
    }

let private qualify () =
    async {
        let directory =
            Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "artifacts/verification/growth",
                Guid.NewGuid().ToString("N")
            )

        Directory.CreateDirectory directory |> ignore

        let atomic name value =
            let path = Path.Combine(directory, name)
            File.WriteAllBytes(path + ".writing", JsonSerializer.SerializeToUtf8Bytes value)
            File.Move(path + ".writing", path, true)

        let selected = profiles ()
        let mutable count = 0

        let nativeRun =
            Environment.GetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY")
            |> Option.ofObj
            |> Option.map Path.GetFileName

        let state result =
            atomic
                "run.json"
                {|
                    result = result
                    completed = count
                    expected = selected.Length
                    requiredProfiles = 9
                    completeQualification = selected.Length = 9
                    nativeRun = nativeRun
                |}

        state "incomplete"

        try
            do!
                workloads selected (fun report ->
                    atomic (sprintf "workload-%02d.json" count) report
                    count <- count + 1
                    state "incomplete")

            let! resources = cardinality () |> Async.AwaitTask
            atomic "cardinality.json" resources
            state "completed"
        with error ->
            let failure =
                try
                    state "failed"
                    error
                with reportError ->
                    AggregateException(
                        "Qualification and report storage failed.",
                        [| error; reportError |]
                    )
                    :> exn

            return raise failure
    }

[<Tests>]
let tests =
    testSequenced (
        testCaseAsync "synthetic write contention and verifier growth qualification" (qualify ())
    )
