module internal ClaimCore.IntegrationTests.TechnicalWitnessPruneTests

open System
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let pendingPreparationCannotBePruned =
    testCase "[CC-REC-001] unaccepted unrevoked PREPARE is not owner-prunable" (fun _ ->
        setup (fun owner source writer witness principal ->
            let request =
                openRequest (Guid.NewGuid()) ("TECH-R-" + Guid.NewGuid().ToString("N"))

            let context =
                actorContext source witness principal EndpointAction.PrepareNewCase request

            use healthy = protocol owner writer (fun () -> ())

            match
                (recovery source healthy context).Retain(draft request context, cancellation)
                |> await
            with
            | Ok(RecoveryRetain.Created _) -> ()
            | _ -> failtest "Synthetic pending PREPARE must commit."

            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use age =
                new NpgsqlCommand(
                    "UPDATE claimcore.request_preparations SET prepared_at=clock_timestamp()-interval '3 days' "
                    + "WHERE operation_id=@operation",
                    connection
                )

            Sql.uuid age "operation" request.OperationId
            Expect.equal (age.ExecuteNonQuery()) 1 "Only this synthetic PREPARE was aged"

            let result =
                PreparationPruning.prune
                    owner
                    { PreparationPruneOptions.defaults with
                        AbandonedRetentionDays = 1
                    }
                |> completedAdministration

            Expect.equal result.DeletedCount 0 "No terminal authority means no prune"

            Expect.equal
                (rowCount owner "request_preparations" request.OperationId)
                1L
                "Pending primary proof survives"))
