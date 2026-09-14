namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData

/// Durable dismissal closes future execution authority but keeps prior attempt knowledge intact.
module internal RecoveryDismissalStore =
    let private dismissed header value =
        { header with
            Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
        }

    let private existingRevocation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        value
        =
        task {
            let! preparation = readHeader connection (Some transaction) operationId

            return
                match preparation with
                | Some header -> Ok(RecoveryDismissal.AlreadyDismissed(dismissed header value))
                | None -> Ok(RecoveryDismissal.RevokedTombstone(project value))
        }

    let private createRevocation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        =
        task {
            let! preparation = readHeader connection (Some transaction) operationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some header when header.RequestSha256 <> requestSha256 ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some header ->
                let! inserted =
                    insert
                        connection
                        transaction
                        operationId
                        requestSha256
                        RevocationReason.OperatorDismissal

                return
                    match inserted with
                    | Some value -> Ok(RecoveryDismissal.Dismissed(dismissed header value))
                    | None -> Error RecoveryStoreFailure.StoreCorrupt
        }

    let private dismissInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        =
        task {
            do! Sql.lockKeyAsync connection transaction ("operation:" + operationId.ToString("D"))

            let! accepted = StoreData.readOperation connection (Some transaction) operationId

            match accepted with
            | Some(_, fingerprint) when fingerprint <> requestSha256 ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some(receipt, _) -> return Ok(RecoveryDismissal.ObservedAccepted receipt)
            | None ->
                let! revoked = find connection transaction operationId

                match revoked with
                | Some value when not (matches requestSha256 value) ->
                    return Error RecoveryStoreFailure.IdempotencyConflict
                | Some value -> return! existingRevocation connection transaction operationId value
                | None -> return! createRevocation connection transaction operationId requestSha256
        }

    let dismiss
        (dataSource: NpgsqlDataSource)
        (operationId: Guid)
        requestSha256
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryDismissal, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty || String.IsNullOrWhiteSpace(requestSha256) then
                return Error(RecoveryStoreFailure.InvalidInput "operationId")
            else
                let commitStarted = ref false

                try
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    return!
                        withTransaction
                            connection
                            cancellationToken
                            commitStarted
                            (fun transaction ->
                                dismissInTransaction
                                    connection
                                    transaction
                                    operationId
                                    requestSha256)
                with error ->
                    return Error(mutationFailure commitStarted.Value error)
        }
