module ClaimCore.IntegrationTests.PreparationHoldRetentionTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

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

let private prune owner expected =
    let options =
        { PreparationPruneOptions.defaults with
            SettledRetentionDays = 1
            BatchLimit = 1
        }

    match PreparationPruning.prune owner options with
    | AdministrationOutcome.Completed result ->
        Expect.equal result.DeletedCount expected "Hold-bound pruning"
    | _ -> failtest "Owner pruning must have a confirmed outcome."

let private hold (actor: IActorClaimsCore) (request: ClaimCore.Domain.CommandRequest) action =
    let current = review actor request.CaseReference
    let pending = change (Guid.NewGuid()) request.CaseReference current action

    match actor.Lifecycle.Apply(pending, CancellationToken.None) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic hold transition must be witnessed."

let tests =
    testCase
        "[CC-LIFE-001] [CC-REC-001] a witnessed hold retains terminal preparation evidence until release"
        (fun () ->
            setup (fun owner _ _ (runtime: Runtime) proposer _ _ _ _ ->
                let actor = runtime.ForActor proposer
                let request = newRequest ()
                executeAccepted actor request
                let holdId = Guid.NewGuid()

                hold
                    actor
                    request
                    (LifecycleMutation.RecordHold(
                        holdId,
                        "Synthetic preparation evidence hold",
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1.))
                    ))

                age owner request.OperationId
                prune owner 0

                hold
                    actor
                    request
                    (LifecycleMutation.ReleaseHold(holdId, "Synthetic verified release"))

                prune owner 1

                match actor.Execute(request, CancellationToken.None) |> await with
                | SubmissionOutcome.ObservedAccepted receipt ->
                    Expect.equal
                        receipt.OperationId
                        request.OperationId
                        "Accepted authority outlives optional evidence"
                | _ -> failtest "Exact accepted replay remains witnessed after pruning."))
