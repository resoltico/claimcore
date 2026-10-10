module internal ClaimCore.IntegrationTests.RuntimeAuditQuarantineFixture

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

let token = CancellationToken.None
let bound = TimeSpan.FromSeconds 20.

let signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let wait (pending: Task<'a>) =
    pending.WaitAsync(bound).GetAwaiter().GetResult()

let refused (pending: Task<'a>) =
    task {
        try
            let! _ = pending
            failtest "Queued actor access escaped audit quarantine."
        with :? InvalidOperationException ->
            ()
    }

let primaryStillFenced owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction()

    use command =
        new NpgsqlCommand(
            "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE NOWAIT",
            connection,
            transaction
        )

    try
        command.ExecuteScalar() |> ignore
        failtest "Audit released the primary fence before failure publication."
    with :? PostgresException as error ->
        Expect.equal error.SqlState "55P03" "Failure publication still owns the primary fence"

let admittedRuntime app writer principal =
    Runtime.OpenPostgres(
        app,
        writer,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        token
    )
    |> await
    |> accepted
    |> fun runtime -> runtime, runtime.ForActor principal

let acceptedRequest (core: IActorClaimsCore) =
    let request =
        openRequest (Guid.NewGuid()) ("QUARANTINE-" + Guid.NewGuid().ToString("N"))

    match core.Execute(request, token) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> request
    | _ -> failtest "Synthetic accepted evidence is required before the queued audit."

let guard requireHealthy =
    { new IMutationCommitHealth with
        member _.VerifyLocked(_, _, _) = task { requireHealthy () } :> Task
    }

type HealthProbe() =
    let mutable failed = 0
    let mutable preflightCount = 0

    member _.RequireHealthy() =
        if Volatile.Read(&failed) <> 0 then
            invalidOp "Synthetic sticky audit quarantine."

    member _.Fail() =
        Interlocked.Exchange(&failed, 1) |> ignore

    member _.PreflightCount = Volatile.Read(&preflightCount)

    member _.Preflight (check: CancellationToken -> Task<unit>) ct =
        task {
            do! check ct
            Interlocked.Increment(&preflightCount) |> ignore
        }

[<NoEquality; NoComparison>]
type Scenario =
    {
        Owner: string
        Witness: WitnessProtocol
        Runtime: Runtime
        Resources: RuntimeResources
        Safety: RuntimeSafetySupervisor
        Principal: PrincipalKey
        Accepted: ClaimCore.Domain.CommandRequest
    }

[<NoEquality; NoComparison>]
type Pause =
    {
        Fenced: TaskCompletionSource<unit>
        FailAudit: TaskCompletionSource<unit>
        Publishing: TaskCompletionSource<unit>
        Publish: TaskCompletionSource<unit>
    }

let grantCaseWork source witness principal =
    let registry = new ActorGrantRegistry(source, witness)

    for role in [ Role.CaseEditor; Role.RecoveryOperator ] do
        registry.SetGrant(
            principal,
            actorId source principal,
            {
                Role = role
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

let withScenario owner app writer witness action =
    let principal = human "queued-audit-owner"
    provision owner witness principal |> applied
    use source = RuntimeDataSource.create app
    grantCaseWork source witness principal
    let runtime, initial = admittedRuntime app writer principal
    use runtime = runtime
    let accepted = acceptedRequest initial
    use resources = new RuntimeResources(app, artifactKeyRingFile ())
    resources.Attach witness

    resources.AttachSuppression(
        { new IDisposable with
            member _.Dispose() = ()
        },
        FixturePrivateFiles.syntheticCommitments witness.Identity
    )

    let safety = RuntimeSafetySupervisor(resources)
    safety.Initialize(token) |> await |> ignore

    action
        {
            Owner = owner
            Witness = witness
            Runtime = runtime
            Resources = resources
            Safety = safety
            Principal = principal
            Accepted = accepted
        }

let admissionFor (scenario: Scenario) (probe: HealthProbe) =
    let safety = scenario.Safety

    let useGate =
        { RuntimeAdmissionFixture.gate (fun () -> ()) with
            RequireCaseRead = probe.Preflight safety.RequireCaseRead
            RequireCaseMutation = probe.Preflight safety.RequireCaseMutation
            RequireAuthorityRead = probe.Preflight safety.RequireAuthorityRead
            RequireAuthoritySetup = probe.Preflight safety.RequireAuthoritySetup
            RequireAuditTrust = probe.RequireHealthy
            CommitHealth = guard probe.RequireHealthy
            AuthorityHealth = guard probe.RequireHealthy
        }

    new RuntimeAdmission(
        { new IDisposable with
            member _.Dispose() = ()
        },
        bound,
        safety.RequireCurrent,
        safety.AcquireReadFence,
        useGate
    )

let startAudit scenario (probe: HealthProbe) pause =
    let mutable startedPublication = 0

    RuntimeFullAudit.runGuardedWith
        scenario.Resources
        (fun () -> Task.CompletedTask)
        (fun () ->
            task {
                pause.Fenced.TrySetResult() |> ignore
                do! pause.FailAudit.Task
                invalidOp "Synthetic fenced audit divergence."
            }
            :> Task)
        (fun _ ->
            if Interlocked.Exchange(&startedPublication, 1) = 0 then
                pause.Publishing.TrySetResult() |> ignore
                wait pause.Publish.Task
                probe.Fail())
        token
