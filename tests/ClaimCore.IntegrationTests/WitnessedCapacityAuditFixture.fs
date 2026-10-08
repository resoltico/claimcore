module internal ClaimCore.IntegrationTests.WitnessedCapacityAuditFixture

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.AuthorityOperationFenceFixture

let private grantWork app witness principal =
    use source = RuntimeDataSource.create app
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    for role in [ Role.CaseEditor; Role.RecoveryOperator; Role.RecoveryExporter ] do
        registry.SetGrant(
            principal,
            actorId grants principal,
            {
                Role = role
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

let withRuntime action =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let principal = human "witnessed-capacity-owner"
        provision owner witness principal |> applied
        grantWork app witness principal

        use runtime =
            Runtime.OpenPostgres(
                pooled app 2,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        let core = runtime.ForActor principal
        use auditResources = resources app witness
        action owner core auditResources)

let startVolumeAuditWithin (ceiling: TimeSpan) (auditResources: RuntimeResources) =
    // Complete paged replay has a functional workload ceiling, not a latency assertion.
    // Short authority/fence coordination controls retain their separate 15-second bound.
    task {
        use workload = new CancellationTokenSource(ceiling)
        return! RuntimeFullAudit.run auditResources workload.Token
    }

let runVolumeAudit auditResources =
    startVolumeAuditWithin (TimeSpan.FromMinutes 2.) auditResources |> await

let private joinAuditAndActors (audit: Task) actors =
    let joined = Task.WhenAll(Array.append [| audit |] actors)

    try
        joined.GetAwaiter().GetResult()
    with :? OperationCanceledException when joined.IsCanceled ->
        ()

let holdAudit owner (core: IActorClaimsCore) auditResources reference afterQueued =
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 15.)
    let fenced = signal ()
    let hold = signal ()
    let mutable actors = [||]

    let audit =
        RuntimeFullAudit.runWith
            auditResources
            (fun () -> Task.CompletedTask)
            (fun () ->
                fenced.SetResult()
                hold.Task.WaitAsync(timeout.Token) :> Task)
            timeout.Token

    try
        Expect.isTrue (fenced.Task.Wait(2000)) "Audit holds its stable cutoff."

        let mutation =
            Task.Run<SubmissionOutcome>(fun () ->
                core.Execute(
                    openRequest (Guid.NewGuid()) (reference + "-NEXT"),
                    CancellationToken.None
                ))

        actors <- [| mutation :> Task |]

        let read =
            Task.Run<QueryOutcome<Lookup<CurrentCase, string>>>(fun () ->
                core.Get(reference, CancellationToken.None))

        actors <- [| mutation :> Task; read :> Task |]
        waitForDatabaseLock owner "transactionid"
        Expect.isFalse mutation.IsCompleted "Mutation waits behind audit authority."
        Expect.isFalse read.IsCompleted "Disclosure waits behind the stable audit."
        afterQueued (timeout, audit, mutation, read)
    finally
        hold.TrySetResult() |> ignore
        timeout.Cancel()
        joinAuditAndActors audit actors

let resumeAfterCancellation
    auditResources
    ((timeout: CancellationTokenSource), audit, mutation, read)
    =
    timeout.Cancel()

    try
        completed audit |> ignore
        failtest "Audit must report cancellation."
    with :? OperationCanceledException ->
        ()

    match completed mutation with
    | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
    | _ -> failtest "Admitted mutation must keep its accepted result after audit cancellation."

    match completed read with
    | QueryOutcome.Succeeded(Lookup.Found _) -> ()
    | _ -> failtest "Queued read must resume without pool exhaustion."

    let final = runVolumeAudit auditResources
    Expect.equal final.Cases 56L "Post-contention audit includes the accepted operation."
