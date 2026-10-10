module ClaimCore.IntegrationTests.RecoverySnapshotTests

open System
open System.Threading.Tasks
open Expecto
open Npgsql
open Microsoft.Extensions.Logging
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private grant source witness principal =
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        principal,
        ActorGrantTestSupport.actorId (source) principal,
        {
            Role = Role.RecoveryOperator
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> ActorGrantTestSupport.applied

let private age owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET recorded_at=clock_timestamp()-interval '3 days' WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Expect.equal (command.ExecuteNonQuery()) 1 "Exact synthetic acceptance is aged"

let private waitForHeader
    (pause: RecoverySnapshotGate.Gate)
    (read: Task<Result<RecoveryStoreInspection option, RecoveryStoreFailure>>)
    =
    try
        pause.Wait()
    with _ ->
        if read.IsCompleted then
            match read.GetAwaiter().GetResult() with
            | Error failure ->
                failtest ("Synthetic reader refused before its gate: " + string failure)
            | _ -> failtest "Synthetic reader completed before its gate."
        else
            failtest "Synthetic reader remains active without its gate."

let private inspectAcrossPrune
    owner
    app
    (source: NpgsqlDataSource)
    (witness: WitnessProtocol)
    principal
    (request: ClaimCore.Domain.CommandRequest)
    =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let context =
        gate.Operation(principal, EndpointAction.RecoveryInspect, request.OperationId, cancellation)
        |> await
        |> Option.defaultWith (fun () -> failtest "Recovery inspection is authorised.")

    use pause = new RecoverySnapshotGate.Gate()
    let builder = NpgsqlDataSourceBuilder(app)
    builder.UseLoggerFactory(pause :> ILoggerFactory) |> ignore
    use measured = builder.Build()
    let port = recovery measured witness context

    let read =
        Task.Run<Result<RecoveryStoreInspection option, RecoveryStoreFailure>>(
            Func<Task<Result<RecoveryStoreInspection option, RecoveryStoreFailure>>>(fun () ->
                port.Inspect(request.OperationId, None, 10, cancellation))
        )

    try
        waitForHeader pause read

        let options =
            { PreparationPruneOptions.defaults with
                SettledRetentionDays = 1
                BatchLimit = 1
            }

        let pruned = PreparationPruning.prune owner options |> completedAdministration

        Expect.equal
            pruned.DeletedCount
            1
            "Owner deletion commits while the header snapshot is held"
    finally
        pause.Release()

    match read.WaitAsync(TimeSpan.FromSeconds(30.)) |> await with
    | Ok(Some(RecoveryStoreInspection.Retained(_, evidence, RecoveryAuthority.AcceptedAuthority))) ->
        Expect.equal
            evidence.Items.Length
            1
            "Attempt evidence belongs to the retained header's snapshot"
    | _ -> failtest "Concurrent pruning cannot create a mixed recovery view."

    match port.Inspect(request.OperationId, None, 10, cancellation) |> await with
    | Ok None -> ()
    | _ -> failtest "A later snapshot observes the completed pruning."

let tests =
    testCase
        "[CC-REC-001] recovery inspection uses one snapshot across concurrent owner pruning"
        (fun () ->
            ActorGrantTestSupport.withAuthorityRuntimeDatabase (fun owner app _ witness ->
                let principal = ActorGrantTestSupport.human "snapshot-owner"

                ActorGrantTestSupport.provision owner witness principal
                |> ActorGrantTestSupport.applied

                use source = RuntimeDataSource.create app
                let registry = new ActorGrantRegistry(source, witness)

                registry.SetGrant(
                    principal,
                    ActorGrantTestSupport.actorId (source) principal,
                    {
                        Role = Role.CaseEditor
                        Scope = GrantScope.Installation
                    },
                    true
                )
                |> await
                |> ActorGrantTestSupport.applied

                grant source witness principal
                let request = newRequest ()

                let context =
                    actorContext source witness principal EndpointAction.ExecuteNewCase request

                FixtureCommandExecution.executeRequest source witness context clock request
                |> await
                |> accepted
                |> ignore

                age owner request.OperationId
                inspectAcrossPrune owner app source witness principal request))
