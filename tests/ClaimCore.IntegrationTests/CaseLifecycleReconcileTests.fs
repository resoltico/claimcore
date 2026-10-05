module ClaimCore.IntegrationTests.CaseLifecycleReconcileTests

open System
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.FixtureWitnessWriterStore

let private cancellation = CancellationToken.None

let private faultProtocol owner writer =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic installation identity is absent."

    let identity =
        {
            InstallationId = reader.GetGuid(0)
            LineageId = reader.GetGuid(1)
            Epoch = reader.GetInt64(2)
        }

    let store = FixtureWitnessWriterStore.current writer identity

    let keyId, _ = (store.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult())

    new WitnessProtocol(
        store,
        new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody,
        identity,
        fun () -> raise (TimeoutException("Synthetic postcommit settlement loss"))
    )

let private context source (witness: WitnessProtocol) principal reference =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Case(principal, EndpointAction.VoidCase, reference, cancellation)
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic steward context is absent.")

let private uncertainVoid owner source writer proposer (actor: IActorClaimsCore) =
    let opened = openRequest (Guid.NewGuid()) ("LIFE-R-" + Guid.NewGuid().ToString("N"))
    executeAccepted actor opened
    let current = review actor opened.CaseReference

    let action =
        change
            (Guid.NewGuid())
            opened.CaseReference
            current
            (LifecycleMutation.VoidDataEntryError "Synthetic uncertain void")

    use fault = faultProtocol owner writer

    let storage =
        PostgresCaseLifecycleStore(
            source,
            fault,
            FixturePrivateFiles.syntheticCommitments fault.Identity
        )
        :> ICaseLifecycleStore

    match
        storage.Apply(context source fault proposer opened.CaseReference, action, cancellation)
        |> await
    with
    | LifecycleWriteOutcome.Unconfirmed id when id = action.EventId -> ()
    | _ -> failtest "Lost settlement must stay unconfirmed."

    action

let private reconcileCommitted owner source witness (action: LifecycleChange) =
    use ownerConnection = new NpgsqlConnection(owner)
    ownerConnection.Open()
    use audit = RuntimeDatabase.openConnection source

    Expect.throws
        (fun () -> DataAudit.run audit witness cancellation |> await |> ignore)
        "Unsettled lifecycle authority must quarantine full audit"

    match
        CaseLifecycleReconcile.reconcile ownerConnection witness action.EventId cancellation
        |> await
    with
    | LifecycleReconcileOutcome.Settled id when id = action.EventId -> ()
    | _ -> failtest "Committed row must reconcile exactly."

    DataAudit.run audit witness cancellation |> await |> ignore

    let orphan = Guid.NewGuid()
    let bytes = Encoding.UTF8.GetBytes("synthetic orphan lifecycle intent")

    (witness.BeginAuthority(orphan, bytes, None, CancellationToken.None).GetAwaiter().GetResult())
    |> ignore

    match
        CaseLifecycleReconcile.reconcile ownerConnection witness orphan cancellation
        |> await
    with
    | LifecycleReconcileOutcome.PrimaryAbsentUnknown id when id = orphan -> ()
    | _ -> failtest "An orphan intent must remain unknown."

let private uncertainThenOwnerReconcile =
    testCase
        "[CC-LIFE-001] owner reconciles committed lifecycle intent without inventing an orphan outcome"
        (fun _ ->
            setup (fun owner (source, _) witness runtime proposer _ _ _ writer ->
                let action =
                    uncertainVoid owner source writer proposer (runtime.ForActor proposer)

                reconcileCommitted owner source witness action))

let tests = testList "case lifecycle reconciliation" [ uncertainThenOwnerReconcile ]
