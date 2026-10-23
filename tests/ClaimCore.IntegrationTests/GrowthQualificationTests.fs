module ClaimCore.IntegrationTests.GrowthQualificationTests

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
open ClaimCore.TestSupport.StoreContractTests

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
                actor.ExecuteAuthority(
                    authorityRequest (id (100000 + index * 2)) principal AuthorityAct.RegisterActor,
                    token
                )

            acceptedAuthority registered

            let! granted =
                grant
                    actor
                    (id (100001 + index * 2))
                    principal
                    Role.CaseReader
                    GrantTarget.Installation
                    true

            acceptedAuthority granted
    }

let private openCases (actor: IActorClaimsCore) count =
    task {
        for index in 0 .. count - 1 do
            let! result =
                actor.Execute(
                    request
                        (1000 + index)
                        ("synthetic-growth-" + string index)
                        0L
                        (BusinessCommand.Open registration),
                    token
                )

            accepted result |> ignore
    }

let private writeLane (actor: IActorClaimsCore) workload lane =
    task {
        let durations = ResizeArray<float>()
        let revisions = Array.create workload.Cases 1L

        for index in lane .. workload.Writers .. workload.Writes - 1 do
            let target = index % workload.Cases
            let revision = revisions[target]

            let command =
                if revision % 2L = 1L then
                    BusinessCommand.Close
                else
                    BusinessCommand.Reopen

            let authored =
                request (200000 + index) ("synthetic-growth-" + string target) revision command

            let watch = Stopwatch.StartNew()
            let! result = actor.Execute(authored, token)
            accepted result |> ignore
            durations.Add watch.Elapsed.TotalMilliseconds
            revisions[target] <- revision + 1L

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
                (fun _ -> LedgerDecision.Refuse Rejection.ResourceUnavailable),
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
        cases = workload.Cases
        actors = workload.Actors + 1
        grants = workload.Actors + 3
        writers = workload.Writers
        ledgerEntries = position.Sequence
        seconds = elapsed
        writesPerSecond = float workload.Writes / elapsed
        p50Milliseconds = latencies[latencies.Length / 2]
        p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
        maximumMilliseconds = Array.last latencies
        verificationMilliseconds = verificationMilliseconds
        managedBytesBeforeVerification = beforeMemory
        managedBytesAfterVerification = GC.GetTotalMemory false
        processPeakWorkingSetBytes = runner.PeakWorkingSet64
        sampledPeakManagedBytes = peakManaged
        sampledPeakWorkingSetBytes = peakWorking
        lockTimeoutMilliseconds = timeoutMilliseconds
        unexpectedErrors = 0
    |}

let private measure workload (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        do! openCases actor workload.Cases
        let watch = Stopwatch.StartNew()

        let writes =
            [| for lane in 0 .. workload.Writers - 1 -> writeLane actor workload lane |]

        let authorityWork = authority actor (subjects workload.Actors)

        do!
            Task.WhenAll(
                Array.append
                    (writes |> Array.map (fun work -> work :> Task))
                    [| authorityWork :> Task |]
            )

        let lanes = writes |> Array.map _.Result
        let elapsed = watch.Elapsed.TotalSeconds
        let latencies = lanes |> Array.concat |> Array.sort
        let! timeoutMilliseconds = timeout database

        let! verification = verifyMeasured database
        let position, _, _, _, _ = verification

        Expect.equal
            position.Sequence
            (int64 (3 + workload.Cases + workload.Writes + workload.Actors * 2))
            "Every accepted write is counted"

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
        | QueryOutcome.Succeeded page ->
            Expect.equal
                page.Entries.Length
                (min 50 (1 + workload.Writes / workload.Cases))
                "Bounded full history is complete"
        | _ -> failtest "Growth history read failed"

        return report workload elapsed latencies timeoutMilliseconds verification
    }

let private workloads completed =
    async {
        for writes, cases, actors in
            [ 64, 4, 1; 256, 16, 4; 1024, 32, 16; 256, 4, 1; 256, 32, 1; 256, 4, 16 ] do
            for writers in [ 1; 4 ] do
                let! database = PostgresFixture.create () |> Async.AwaitTask

                try
                    let! report =
                        measure
                            {
                                Writes = writes
                                Cases = cases
                                Actors = actors
                                Writers = writers
                            }
                            database
                        |> Async.AwaitTask

                    completed report
                finally
                    database.Fixture.Dispose()

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
                    expected = 12
                    nativeRun = nativeRun
                |}

        state "incomplete"

        try
            do!
                workloads (fun report ->
                    atomic (sprintf "workload-%02d.json" count) report
                    count <- count + 1
                    state "incomplete")

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
