module ClaimCore.IntegrationTests.WitnessProtocolTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Postgres.WitnessProtocolReconciliation
open ClaimCore.Hosting
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

let private identity () =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic primary identity is required."

    {
        InstallationId = reader.GetGuid(0)
        LineageId = reader.GetGuid(1)
        Epoch = reader.GetInt64(2)
    }

let private count connectionString table operationId =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE operation_id=@operation", connection)

    command.Parameters.AddWithValue("operation", operationId) |> ignore
    command.ExecuteScalar() :?> int64

let private protocol identity fault =
    let store = witnessStore (witnessConnection ()) identity

    let keyId, _ = (store.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult())

    new WitnessProtocol(
        store,
        new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody,
        identity,
        fault
    )

let private openContext source (witness: WitnessProtocol) request =
    let principal = ActorBoundStoreFixture.actorPrincipal ()

    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Command(principal, EndpointAction.ExecuteNewCase, request, CancellationToken.None)
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic editor lacks witnessed OPEN authority.")

let private executeWithSettlementFailure
    (source: NpgsqlDataSource)
    (installation: Identity)
    (operation: CommandRequest)
    =
    use faulty =
        protocol installation (fun () ->
            if
                count (witnessOwnerConnection ()) "claimcore_witness.journal" operation.OperationId >
                    0L
            then
                raise (TimeoutException("synthetic settlement interruption")))

    let actor = openContext source faulty operation


    match
        FixtureCommandExecution.executeRequest source faulty actor clock operation
        |> await
    with
    | Error(CoreFailure.CommitOutcomeUnknown value) ->
        Expect.equal value operation.OperationId "No definite receipt after settlement failure"
    | _ -> failtest "Postcommit settlement failure must be unknown."

let private assertAcceptedIntent (operation: CommandRequest) =
    Expect.equal
        (count (adminConnection ()) "claimcore.case_changes" operation.OperationId)
        1L
        "Primary accepted row committed once"

    Expect.equal
        (count (witnessOwnerConnection ()) "claimcore_witness.journal" operation.OperationId)
        1L
        "Only the witnessed intent exists"

let private retryAccepted
    (source: NpgsqlDataSource)
    (installation: Identity)
    (operation: CommandRequest)
    =
    use healthy = protocol installation (fun () -> ())
    let actor = openContext source healthy operation


    match
        FixtureCommandExecution.executeRequest source healthy actor clock operation
        |> await
    with
    | Ok receipt -> Expect.isTrue receipt.Replayed "Exact retry returns original accepted receipt"
    | Error _ -> failtest "Exact primary/witness reconciliation must settle."

    Expect.equal
        (count (adminConnection ()) "claimcore.case_changes" operation.OperationId)
        1L
        "Retry never duplicates primary effect"

    Expect.equal
        (count (witnessOwnerConnection ()) "claimcore_witness.journal" operation.OperationId)
        2L
        "Retry appends one settled proof"

let private retryAfterCompetingSettlement
    (source: NpgsqlDataSource)
    (installation: Identity)
    (operation: CommandRequest)
    =
    use competing = protocol installation (fun () -> ())

    use racing =
        protocol installation (fun () ->
            use primary = new NpgsqlConnection(adminConnection ())
            primary.Open()
            use transaction = primary.BeginTransaction()

            (competing
                .ReconcileAccepted(
                    primary,
                    transaction,
                    operation.OperationId,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult())

            transaction.Rollback())

    let actor = openContext source racing operation


    match
        FixtureCommandExecution.executeRequest source racing actor clock operation
        |> await
    with
    | Ok receipt ->
        Expect.isTrue receipt.Replayed "Competing exact settlement returns the retained receipt"
    | Error _ -> failtest "A competing exact settlement must be verified by readback."

    Expect.equal
        (count (adminConnection ()) "claimcore.case_changes" operation.OperationId)
        1L
        "Competing settlement cannot duplicate the primary effect"

    Expect.equal
        (count (witnessOwnerConnection ()) "claimcore_witness.journal" operation.OperationId)
        2L
        "Competing settlement retains one intent and one exact outcome"

let private acceptanceAfterSettlementFailure =
    testCase
        "[CC-WIT-001] postcommit witness outage stays unknown then exact retry settles"
        (fun _ ->
            let operation =
                openRequest (Guid.NewGuid()) ("WITNESS-" + Guid.NewGuid().ToString("N"))

            let installation = identity ()
            use source = RuntimeDataSource.create (appConnection ())
            executeWithSettlementFailure source installation operation
            assertAcceptedIntent operation
            retryAccepted source installation operation

            let racingOperation =
                openRequest (Guid.NewGuid()) ("WITNESS-RACE-" + Guid.NewGuid().ToString("N"))

            executeWithSettlementFailure source installation racingOperation
            assertAcceptedIntent racingOperation
            retryAfterCompetingSettlement source installation racingOperation)

let private beginOrphanIntent
    (witness: WitnessProtocol)
    (source: NpgsqlDataSource)
    (operation: CommandRequest)
    =
    let prepared =
        Operation.prepare operation
        |> Result.defaultWith (fun _ -> failtest "Synthetic request must prepare.")

    let context = clock.Capture()

    let claim =
        Claim.decide context.EffectiveBusinessDate operation None
        |> Result.defaultWith (fun _ -> failtest "Synthetic request must decide.")

    let actor = openContext source witness operation

    let caseId =
        actor.CaseId
        |> Option.defaultWith (fun () -> failtest "OPEN case ID is absent.")

    let attribution: ExecutionAttribution =
        {
            Command =
                {
                    Actor = actor.Binding
                    CaseId = caseId
                }
            PreparerActorId = actor.Binding.ActorId
            ImporterActorId = None
            Phase = AttemptActorPhase.NormalSubmit
        }

    WitnessAcceptedProtocol.beginAccepted witness prepared context caseId attribution claim
    |> ignore

    actor

let private intentWithoutPrimaryReceipt =
    testCase "[CC-WIT-001] orphan intent stays unknown without primary effect" (fun _ ->
        let operation =
            openRequest (Guid.NewGuid()) ("ORPHAN-" + Guid.NewGuid().ToString("N"))

        let installation = identity ()
        use witness = protocol installation (fun () -> ())
        use source = RuntimeDataSource.create (appConnection ())
        let actor = beginOrphanIntent witness source operation


        match
            FixtureCommandExecution.executeRequest source witness actor clock operation
            |> await
        with
        | Error(CoreFailure.CommitOutcomeUnknown value) ->
            Expect.equal value operation.OperationId "Intent with absent receipt remains unknown"
        | _ -> failtest "Orphan intent must not be replayed or rejected as uncommitted."

        Expect.equal
            (count (adminConnection ()) "claimcore.case_changes" operation.OperationId)
            0L
            "No primary acceptance was fabricated"

        Expect.equal
            (count (witnessOwnerConnection ()) "claimcore_witness.journal" operation.OperationId)
            1L
            "Original intent remains for reconciliation")

let private wrongKeyStartup =
    testCase "[CC-WIT-001] wrong witness key refuses runtime opening before case work" (fun _ ->
        let wrongKey = RandomNumberGenerator.GetBytes(32)

        let result =
            Runtime.OpenPostgres(
                appConnection (),
                witnessConnection (),
                wrongKey,
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await

        match result with
        | Error _ -> ()
        | Ok runtime ->
            (runtime :> IDisposable).Dispose()
            failtest "Wrong witness key must never open case work.")


let tests =
    testList
        "witnessed primary commit"
        [
            acceptanceAfterSettlementFailure
            intentWithoutPrimaryReceipt
            wrongKeyStartup
        ]
