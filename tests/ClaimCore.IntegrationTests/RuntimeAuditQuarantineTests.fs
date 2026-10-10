module ClaimCore.IntegrationTests.RuntimeAuditQuarantineTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

open ClaimCore.IntegrationTests.RuntimeAuditQuarantineFixture

let private queuedLanes (core: IActorClaimsCore) (scenario: Scenario) =
    let accepted = scenario.Accepted

    let next =
        openRequest (Guid.NewGuid()) ("AFTER-AUDIT-" + Guid.NewGuid().ToString("N"))

    [
        core.Get(accepted.CaseReference, token) |> refused
        core.ObserveOperation(accepted.OperationId, token) |> refused
        core.Recovery.Inspect(accepted.OperationId, None, 10, token) |> refused
        core.Management.Observe(Guid.NewGuid(), token) |> refused
        core.Lifecycle.Review(accepted.CaseReference, token) |> refused
        core.Execute(next, token) |> refused
        core.Management.SetGrant(
            Guid.NewGuid(),
            scenario.Principal,
            Role.CaseEditor,
            GrantTarget.Installation,
            false,
            token
        )
        |> refused
    ]

let private acceptanceRemains scenario =
    use observed = new NpgsqlConnection(scenario.Owner)
    observed.Open()

    use retained =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_changes WHERE operation_id=@operation",
            observed
        )

    retained.Parameters.AddWithValue("operation", scenario.Accepted.OperationId)
    |> ignore

    Expect.equal
        (retained.ExecuteScalar() :?> int64)
        1L
        "Withheld disclosure preserves exact committed acceptance"

let private verifyQueued
    scenario
    (probe: HealthProbe)
    pause
    (queued: Task<unit> list)
    audit
    cutoff
    =
    try
        let elapsed = System.Diagnostics.Stopwatch.StartNew()

        while probe.PreflightCount < queued.Length && elapsed.Elapsed < bound do
            Thread.Yield() |> ignore

        Expect.equal
            probe.PreflightCount
            queued.Length
            "Every queued lane passed preflight before audit failure"

        pause.FailAudit.TrySetResult() |> ignore
        wait pause.Publishing.Task
        primaryStillFenced scenario.Owner

        Expect.isTrue
            (queued |> List.forall (fun pending -> not pending.IsCompleted))
            "Publication precedes release and access"

        pause.Publish.TrySetResult() |> ignore

        Expect.throwsT<InvalidOperationException>
            (fun () -> wait audit |> ignore)
            "Fenced audit failure survives publication"

        queued |> List.iter wait

        Expect.equal
            (scenario.Witness.Snapshot(token) |> await).TipSequence
            cutoff
            "Queued writes gain no new witness authority"

        acceptanceRemains scenario
    finally
        pause.FailAudit.TrySetResult() |> ignore
        pause.Publish.TrySetResult() |> ignore

let private queuedReadiness scenario =
    let probe = HealthProbe()

    let pause =
        {
            Fenced = signal ()
            FailAudit = signal ()
            Publishing = signal ()
            Publish = signal ()
        }

    let audit = startAudit scenario probe pause
    wait pause.Fenced.Task

    let observation =
        RuntimeReadiness.observe scenario.Safety probe.RequireHealthy token

    try
        pause.FailAudit.TrySetResult() |> ignore
        wait pause.Publishing.Task
        primaryStillFenced scenario.Owner
        Expect.isFalse observation.IsCompleted "Readiness waits behind the acquired audit barrier"
        pause.Publish.TrySetResult() |> ignore

        Expect.throwsT<InvalidOperationException>
            (fun () -> wait audit |> ignore)
            "Audit failure remains visible"

        Expect.equal
            (wait observation)
            ("UNKNOWN", "QUARANTINED", false)
            "Queued readiness cannot publish a trusted scope or readiness"
    finally
        pause.FailAudit.TrySetResult() |> ignore
        pause.Publish.TrySetResult() |> ignore

let private exercise scenario =
    let probe = HealthProbe()
    use admission = admissionFor scenario probe
    let core = scenario.Runtime.ForActorWithAdmission(scenario.Principal, admission)

    let pause =
        {
            Fenced = signal ()
            FailAudit = signal ()
            Publishing = signal ()
            Publish = signal ()
        }

    let cutoff = (scenario.Witness.Snapshot(token) |> await).TipSequence
    let audit = startAudit scenario probe pause
    wait pause.Fenced.Task
    let queued = queuedLanes core scenario
    verifyQueued scenario probe pause queued audit cutoff

let private withAuditScenario action =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        withScenario owner app writer witness action)

let tests =
    testList
        "scheduled audit fenced quarantine"
        [
            testCase
                "[CC-AUDIT-001] queued reads and writes cannot cross failure publication and committed history survives"
                (fun () -> withAuditScenario exercise)
            testCase
                "[CC-AUDIT-001] queued readiness cannot escape quarantined authority"
                (fun () -> withAuditScenario queuedReadiness)
        ]
