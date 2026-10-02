module ClaimCore.IntegrationTests.AuthorityExpirationTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private ct = CancellationToken.None

// Independent server observations: do not derive the expiry oracle from the production reader.
let private databaseUtc (connection: NpgsqlConnection) (transaction: NpgsqlTransaction | null) =
    use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)
    DateTimeOffset(command.ExecuteScalar() :?> DateTime)

let private waitUntil message condition =
    let budget = Stopwatch.StartNew()

    while not (condition ()) && budget.Elapsed < TimeSpan.FromSeconds 15. do
        Thread.Sleep 20

    Expect.isTrue (condition ()) message

let private afterExpiry owner expiry dispatch =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction()
    ActorGrantRead.lockRevision connection transaction true ct |> await |> ignore
    let pending: Task<LifecycleWriteOutcome> = dispatch ()

    try
        waitUntil "Writer actually waits on the authoritative lock" (fun () ->
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity a "
                    + "WHERE pg_backend_pid()=ANY(pg_blocking_pids(a.pid)))",
                    connection,
                    transaction
                )

            command.ExecuteScalar() :?> bool)

        waitUntil "Independent database time reaches expiry" (fun () ->
            databaseUtc connection transaction >= expiry)
    finally
        transaction.Rollback()
        pending.GetAwaiter().GetResult() |> ignore

    pending.GetAwaiter().GetResult()

let private context source (witness: WitnessProtocol) principal action reference =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Case(principal, action, reference, ct)
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic lifecycle authority is available.")

let private target owner (runtime: Runtime) proposer ungranted =
    let actor = runtime.ForActor proposer
    let request, current = voidCase owner actor (runtime.ForActor ungranted)

    change
        (Guid.NewGuid())
        request.CaseReference
        current
        (LifecycleMutation.ReinstateVoided "Synthetic reviewed correction")

let private expiry owner seconds =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    (databaseUtc connection null).AddSeconds(seconds)

let private delayedApproval () =
    setup (fun owner (source, _) witness runtime proposer first _ ungranted _ ->
        let change = target owner runtime proposer ungranted

        let actor =
            context source witness first EndpointAction.ApproveLifecycle change.CaseReference

        let store =
            PostgresCaseLifecycleStore(
                source,
                witness,
                FixturePrivateFiles.syntheticCommitments witness.Identity
            )
            :> ICaseLifecycleStore

        let id = Guid.NewGuid()
        let deadline = expiry owner 3.

        let outcome =
            afterExpiry owner deadline (fun () -> store.Approve(actor, change, id, deadline))

        Expect.equal
            outcome
            (LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch)
            "Expired new approval cannot be issued after lock delay"

        use connection = new NpgsqlConnection(owner)
        connection.Open()

        use command =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.case_lifecycle_approvals WHERE approval_id=@id",
                connection
            )

        Sql.uuid command "id" id
        Expect.equal (command.ExecuteScalar() :?> int64) 0L "No expired authority was persisted")

let private delayedConsumption () =
    setup (fun owner (source, _) witness runtime proposer first second ungranted _ ->
        let change = target owner runtime proposer ungranted
        let deadline = expiry owner 8.
        let approvals = [ first, Guid.NewGuid(); second, Guid.NewGuid() ]

        for principal, id in approvals do
            match
                (runtime.ForActor principal).Lifecycle.Approve(change, id, deadline, ct)
                |> await
            with
            | LifecycleWriteOutcome.Applied _ -> ()
            | _ -> failtest "Positive approval control must precede the expiry delay."

        let actor =
            context source witness proposer EndpointAction.ReinstateCase change.CaseReference

        let store =
            PostgresCaseLifecycleStore(
                source,
                witness,
                FixturePrivateFiles.syntheticCommitments witness.Identity
            )
            :> ICaseLifecycleStore

        let outcome = afterExpiry owner deadline (fun () -> store.Apply(actor, change))

        Expect.equal
            outcome
            (LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalRequired)
            "Expired approvals cannot authorize a delayed decision"

        Expect.equal
            (review (runtime.ForActor proposer) change.CaseReference).Disposition
            CaseDisposition.VoidedDataEntryError
            "No reinstatement occurred"

        for principal, id in approvals do
            match
                (runtime.ForActor principal).Lifecycle.Approve(change, id, deadline, ct)
                |> await
            with
            | LifecycleWriteOutcome.Applied(observed, _, _) ->
                Expect.equal observed id "Historical receipt retains exact identity"
            | _ -> failtest "Expiry cannot erase a witnessed approval receipt.")

let tests =
    testList
        "authority expiration under lock"
        [
            testCase
                "[CC-LIFE-001] approval issuance uses current database time after lock delay"
                delayedApproval
            testCase
                "[CC-LIFE-001] delayed approval consumption refuses while expired exact readback survives"
                delayedConsumption
        ]
