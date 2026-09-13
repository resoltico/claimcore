namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open PreparationData

module internal RecoveryStoreQueries =
    let installationLineage (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) =
        task {
            try
                cancellationToken.ThrowIfCancellationRequested()

                use! connection =
                    RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

                use command =
                    new NpgsqlCommand(
                        "SELECT lineage_id FROM claimcore.installation_lineage",
                        connection
                    )

                let! lineage = command.ExecuteScalarAsync(cancellationToken)
                cancellationToken.ThrowIfCancellationRequested()

                match lineage with
                | :? Guid as value when value <> Guid.Empty -> return Ok value
                | _ -> return Error RecoveryStoreFailure.StoreCorrupt
            with
            | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
            | error -> return Error(fail error)
        }
