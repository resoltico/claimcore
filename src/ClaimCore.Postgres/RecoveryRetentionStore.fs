namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData
open PreparationLifecycleStore
open SubmissionAttemptStore

/// Retention and attempt admission use the operation lock, with capacity acquired before it.
module internal RecoveryRetentionStore =
    let private retainAbsent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        =
        task {
            match PreparationIntegrity.validateDraft draft with
            | Error failure -> return Error failure
            | Ok() ->
                let! count, bytes = readCapacity connection transaction

                if
                    count >= int64 limits.MaximumPreparations
                    || bytes + int64 draft.CanonicalRequest.Length >
                        limits.MaximumCanonicalRequestBytes
                then
                    return Error RecoveryStoreFailure.CapacityExceeded
                else
                    let! inserted = insertPreparation connection transaction draft
                    return Ok(RecoveryRetain.Created inserted)
        }

    let private retainWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        =
        task {
            let! revoked = find connection transaction draft.OperationId

            match revoked with
            | Some value when matches draft.RequestSha256 value ->
                return Ok(RecoveryRetain.Revoked(project value))
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                let! existing = readHeader connection (Some transaction) draft.OperationId

                match existing with
                | Some value when sameImmutable draft value ->
                    return Ok(RecoveryRetain.Existing value)
                | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
                | None -> return! retainAbsent connection transaction limits draft
        }

    let private retainInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        =
        task {
            do! Sql.lockKeyAsync connection transaction "claimcore:request-preparation-capacity"

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + draft.OperationId.ToString("D"))

            let! accepted =
                StoreData.readOperation connection (Some transaction) draft.OperationId

            match accepted with
            | Some(receipt, original) when original = draft.RequestSha256 ->
                return Ok(RecoveryRetain.ObservedAccepted receipt)
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None -> return! retainWithoutAccepted connection transaction limits draft
        }

    let retain
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryRetain, RecoveryStoreFailure>> =
        task {
            match PreparationIntegrity.validateIdentity draft with
            | Error failure -> return Error failure
            | Ok _ ->
                let commitStarted = ref false

                try
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    return!
                        withTransaction
                            connection
                            cancellationToken
                            commitStarted
                            (fun transaction ->
                                retainInTransaction connection transaction limits draft)
                with error ->
                    return Error(mutationFailure commitStarted.Value error)
        }

    let private startWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        (header: RetainedPreparation)
        =
        task {
            let! revoked = find connection transaction operationId

            match revoked with
            | Some value when matches header.RequestSha256 value ->
                return
                    Ok(
                        RecoveryStart.Dismissed
                            { header with
                                Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
                            }
                    )
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    SubmissionAttemptStore.start
                        maximumAttempts
                        connection
                        transaction
                        operationId
                        header
        }

    let private startInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        =
        task {
            do! Sql.lockKeyAsync connection transaction ("operation:" + operationId.ToString("D"))
            let! preparation = readHeader connection (Some transaction) operationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some header ->
                let! accepted = StoreData.readOperation connection (Some transaction) operationId

                match accepted with
                | Some(receipt, fingerprint) when fingerprint = header.RequestSha256 ->
                    return Ok(RecoveryStart.ObservedAccepted receipt)
                | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
                | None ->
                    return!
                        startWithoutAccepted
                            connection
                            transaction
                            maximumAttempts
                            operationId
                            header
        }

    let start
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryStart, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty then
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
                                startInTransaction
                                    connection
                                    transaction
                                    limits.MaximumAttemptsPerOperation
                                    operationId)
                with error ->
                    return Error(mutationFailure commitStarted.Value error)
        }

    let settle
        (dataSource: NpgsqlDataSource)
        (attemptId: Guid)
        outcome
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RecoveryStoreFailure>> =
        task {
            if attemptId = Guid.Empty then
                return Error(RecoveryStoreFailure.InvalidInput "attemptId")
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
                                SubmissionAttemptStore.settle
                                    connection
                                    transaction
                                    attemptId
                                    outcome)
                with error ->
                    return Error(mutationFailure commitStarted.Value error)
        }
