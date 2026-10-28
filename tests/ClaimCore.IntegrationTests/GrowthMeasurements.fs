module ClaimCore.IntegrationTests.GrowthMeasurements

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures


let internal token = CancellationToken.None

type internal Workload =
    {
        Writes: int
        Cases: int
        Actors: int
        Writers: int
    }

let internal subjects count =
    [
        for index in 1..count ->
            PrincipalKey.human "https://issuer.example/" ("synthetic-growth-" + string index)
            |> Result.defaultWith (string >> failtest)
    ]

let internal authority (actor: ServiceContext) principals =
    task {
        for index, principal in principals |> List.indexed do
            let! registered =
                (Service.execute
                    actor
                    (OperationInput.Authority(
                        authorityRequest (id (100000 + index * 20)) principal AuthorityAct.Register
                    ))
                    (token))

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
                    grant
                        actor
                        (id (100001 + index * 20 + offset))
                        principal
                        { Role = role; Scope = scope }
                        true

                acceptedAuthority granted

    }

let internal openCases (actor: ServiceContext) count =
    task {
        for index in 0 .. count - 1 do
            let! result =
                (Service.execute
                    actor
                    (OperationInput.Case(
                        request
                            (1000 + index)
                            ("synthetic-growth-" + string index)
                            0L
                            (AuthoredCaseAct.Open registration)
                    ))
                    (token))

            accepted result |> ignore

    }

let internal act revision =
    let amended =
        (ClaimCore.TestSupport.Inputs.registrationWith
            [ BusinessField.ClaimantName, ("Synthetic revision " + string revision) ]
            (registration))

    match (revision - 1L) % 8L with
    | 0L -> AuthoredCaseAct.AmendRegistration amended
    | 1L ->
        AuthoredCaseAct.Decide(
            ClaimCore.TestSupport.Inputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, ("2026-08-04")
                    BusinessField.PayableAmount, ("100.00")
                    BusinessField.PayableCurrency, ("EUR")
                ]
        )
    | 2L ->
        AuthoredCaseAct.RecordPayment(
            ClaimCore.TestSupport.Inputs.paymentFrom [ BusinessField.PaymentDate, ("2026-08-05") ]
        )
    | 3L -> (AuthoredCaseAct.Bare BareAct.ClearPayment)
    | 4L ->
        AuthoredCaseAct.Correct
            {
                Registration = Amendment.Replace amended
                Decision = Change.Keep
                Payment = Change.Keep
            }
    | 5L -> (AuthoredCaseAct.Bare BareAct.WithdrawDecision)
    | 6L -> (AuthoredCaseAct.Bare BareAct.Close)
    | _ -> (AuthoredCaseAct.Bare BareAct.Reopen)

let internal writeLane (actor: ServiceContext) workload lane =
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

            let! result = (Service.execute actor (OperationInput.Case authored) (token))

            accepted result |> ignore

            durations.Add watch.Elapsed.TotalMilliseconds
            revisions[target] <- revision + 1L
            turn <- turn + 1

        return durations.ToArray()
    }

type internal VerificationMeasurement =
    {
        Position: LedgerPosition
        ElapsedMilliseconds: float
        BaselineManagedBytes: int64
        PeakManagedBytes: int64
        PeakWorkingBytes: int64
    }

let internal verifyMeasured (database: PostgresFixture.Database) =
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
            {
                Position = VerificationSupport.position result
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                BaselineManagedBytes = baseline
                PeakManagedBytes = peakManaged
                PeakWorkingBytes = peakWorking
            }
    }

let internal timeout (database: PostgresFixture.Database) =
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
            :> IStore

        let watch = Stopwatch.StartNew()

        let! result =
            store.Write(
                {
                    WriteOperation.Work =
                        (fun _ ->
                            Task.FromResult(
                                Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                                : Verdict<unit>
                            ))
                    CancellationToken = token
                }
            )

        Expect.equal
            result
            (Error StoreFailure.Unavailable)
            "Lock timeout is a definite unavailable result"

        do! transaction.RollbackAsync()
        return watch.Elapsed.TotalMilliseconds
    }

let internal report
    workload
    elapsed
    (latencies: float array)
    timeoutMilliseconds
    (measurement: VerificationMeasurement)
    =
    let position = measurement.Position
    let verificationMilliseconds = measurement.ElapsedMilliseconds
    let beforeMemory = measurement.BaselineManagedBytes
    let peakManaged = measurement.PeakManagedBytes
    let peakWorking = measurement.PeakWorkingBytes
    use runner = Process.GetCurrentProcess()

    {|
        writes = workload.Writes
        setupAcceptedOperations = 10 + workload.Cases + workload.Actors * 11
        qualificationAcceptedOperations = 1006
        cases = workload.Cases
        actors = workload.Actors + 1
        grants = workload.Actors * 10 + 10
        writers = workload.Writers
        ledgerEntries = Sequence.value position.Sequence
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

let internal history workload (actor: ServiceContext) =
    task {
        let! history =
            (Service.history
                actor
                ({
                    CaseReference = "synthetic-growth-0"
                    AfterCursor = None
                    Limit = 50
                    Detail = HistoryDetail.Full
                })
                (token))

        match history with
        | Outcome.Succeeded page ->
            let operations = (workload.Writes + workload.Writers - 1) / workload.Writers
            let targets = (workload.Cases + workload.Writers - 1) / workload.Writers

            Expect.equal
                page.Entries.Length
                (min 50 (2 + (operations + targets - 1) / targets))
                "Bounded full history is complete"
        | _ -> failtest "Growth history read failed"

    }

let internal verified workload actor database =
    task {
        let! verification = verifyMeasured database
        let position = verification.Position

        Expect.equal
            (Sequence.value position.Sequence)
            (int64 (1016 + workload.Cases + workload.Writes + workload.Actors * 11))
            "Every accepted write is counted"

        do! history workload actor

        return verification
    }

let internal measuredWrites workload database actor protection =
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

let internal measure keyRing workload (database: PostgresFixture.Database) =
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
