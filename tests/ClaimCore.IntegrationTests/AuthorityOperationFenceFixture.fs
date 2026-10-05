module internal ClaimCore.IntegrationTests.AuthorityOperationFenceFixture

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FreshBaselineSupport

let cancellation = CancellationToken.None
let bound = TimeSpan.FromSeconds 15.

let completed (work: Task<'a>) =
    work.WaitAsync(bound).GetAwaiter().GetResult()

let signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let pooled (app: string) size =
    let builder = NpgsqlConnectionStringBuilder(app)
    builder.MaxPoolSize <- size
    builder.MinPoolSize <- 0
    builder.ConnectionString

let resources app witness =
    let value = new RuntimeResources(pooled app 2, artifactKeyRingFile ())
    value.Attach witness

    value.AttachSuppression(
        { new IDisposable with
            member _.Dispose() = ()
        },
        FixturePrivateFiles.syntheticCommitments witness.Identity
    )

    value

let waitForDatabaseLock owner event =
    use observer = new NpgsqlConnection(owner)
    observer.Open()

    use query =
        new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() "
            + "AND wait_event_type='Lock' AND wait_event=@event)",
            observer
        )

    query.Parameters.AddWithValue("event", event) |> ignore
    let elapsed = Stopwatch.StartNew()
    let mutable found = false

    while not found && elapsed.Elapsed < bound do
        found <- query.ExecuteScalar() :?> bool

        if not found then
            Thread.Yield() |> ignore

    Expect.isTrue found "PostgreSQL confirms the queued lock before the negative assertion."

let private observeSettlementFence
    owner
    (witness: WitnessProtocol)
    runtime
    expectedActors
    (release: TaskCompletionSource<unit>)
    write
    token
    =
    Expect.equal
        (scalar owner "SELECT count(*) FROM claimcore.actors" :?> int64)
        expectedActors
        "Primary COMMIT is independently visible."

    let fenced = signal ()

    let pending =
        RuntimeFullAudit.runWith
            runtime
            (fun () -> Task.CompletedTask)
            (fun () ->
                fenced.TrySetResult() |> ignore
                Task.CompletedTask)
            token

    try
        waitForDatabaseLock owner "advisory"
        Expect.isFalse fenced.Task.IsCompleted "Audit cannot fence an operation awaiting W1."
        release.TrySetResult() |> ignore
        completed write |> applied
        let summary = completed pending

        Expect.isTrue
            fenced.Task.IsCompletedSuccessfully
            "Audit proceeds after settled authority releases its session lease."

        Expect.equal
            summary.WitnessCutoff
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
            "Audit includes the settlement rather than an earlier cutoff."
    finally
        release.TrySetResult() |> ignore

        try
            completed pending |> ignore
        with :? OperationCanceledException ->
            ()

let settlementFence owner app writer witness ownerOperation =
    let principal = human "settlement-owner"

    if not ownerOperation then
        provision owner witness principal |> applied

    use runtime = resources app witness
    use entered = new ManualResetEventSlim(false)
    let release = signal ()

    use fault =
        TechnicalWitnessTestSupport.protocol owner writer (fun () ->
            entered.Set()
            completed release.Task)

    let write =
        Task.Run(fun () ->
            if ownerOperation then
                provision owner fault principal
            else
                let registry = new ActorGrantRegistry(runtime.DataSource, fault)
                registry.RegisterActor(principal, human "new-synthetic-actor") |> completed)

    use timeout = new CancellationTokenSource(bound)

    try
        Expect.isTrue (entered.Wait(bound)) "Writer reached W1 after primary COMMIT."
        let expectedActors = if ownerOperation then 1L else 2L
        observeSettlementFence owner witness runtime expectedActors release write timeout.Token
    finally
        release.TrySetResult() |> ignore
        timeout.Cancel()
        completed write |> ignore

let private verifyCaptureLifetime
    owner
    source
    witness
    principal
    (capture: DatabaseBackupCaptureBarrier)
    =
    let next =
        Task.Run(fun () ->
            let registry = new ActorGrantRegistry(source, witness)
            registry.RegisterActor(principal, human "after-capture-actor") |> completed)

    try
        waitForDatabaseLock owner "advisory"

        Expect.isFalse
            next.IsCompleted
            "The retained capture lease blocks the next authority write."
    finally
        (capture :> IDisposable).Dispose()

    completed next |> applied

let private verifyCaptureSettlement
    owner
    source
    (witness: WitnessProtocol)
    principal
    (release: TaskCompletionSource<unit>)
    write
    token
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let pending =
        DatabaseBackupCaptureBarrier.Acquire(
            connection,
            source,
            witness,
            FixturePrivateFiles.syntheticCommitments witness.Identity,
            token
        )

    try
        waitForDatabaseLock owner "advisory"
        Expect.isFalse pending.IsCompleted "Backup capture waits for post-COMMIT actor settlement."
        release.TrySetResult() |> ignore
        completed write |> applied
        use capture = completed pending

        Expect.equal
            capture.Cutoff.WitnessSequence
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
            "Capture includes settled W1."

        let summary = capture.Verify(token) |> completed
        Expect.equal summary.Actors 2L "Capture verifies both committed actors."
        Expect.equal summary.AuthorityEvents 2L "Capture verifies provisioning and registration."

        verifyCaptureLifetime owner source witness principal capture
    finally
        release.TrySetResult() |> ignore

        try
            let capture = completed pending
            (capture :> IDisposable).Dispose()
        with :? OperationCanceledException ->
            ()

let captureSettlementFence owner app writer witness =
    let principal = human "capture-settlement-owner"
    provision owner witness principal |> applied
    use source = RuntimeDataSource.create app
    use entered = new ManualResetEventSlim(false)
    let release = signal ()

    use fault =
        TechnicalWitnessTestSupport.protocol owner writer (fun () ->
            entered.Set()
            completed release.Task)

    let write =
        Task.Run(fun () ->
            let registry = new ActorGrantRegistry(source, fault)
            registry.RegisterActor(principal, human "capture-synthetic-actor") |> completed)

    use timeout = new CancellationTokenSource(bound)

    try
        Expect.isTrue (entered.Wait(bound)) "Actor registration reached W1 after primary COMMIT."

        Expect.equal
            (scalar owner "SELECT count(*) FROM claimcore.actors" :?> int64)
            2L
            "Both committed actors are visible before W1."

        verifyCaptureSettlement owner source witness principal release write timeout.Token
    finally
        release.TrySetResult() |> ignore
        timeout.Cancel()
        completed write |> ignore

let weakerCatalogAfterOpening () =
    withAuthorityRuntimeDatabase (fun owner app _ witness ->
        use runtime = resources app witness
        RuntimeFullAudit.run runtime cancellation |> completed |> ignore

        execute
            owner
            ("ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money; "
             + "ALTER TABLE claimcore.cases ADD CONSTRAINT claimed_money CHECK (claimed_amount >= 0)")

        Expect.throwsT<RuntimeDatabaseMismatch>
            (fun () -> RuntimeFullAudit.run runtime cancellation |> completed |> ignore)
            "A later complete audit rejects a committed same-name weaker CHECK.")
