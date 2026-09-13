namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application
open PreparationData
open PreparationPageStore
open PreparationLifecycleStore
open SubmissionAttemptStore

module private RecoveryMutationLogic =
    let retain
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
                    ("claimcore:request-preparation:" + draft.OperationId.ToString("D"))

            let! existing = readOne connection (Some transaction) draft.OperationId

            match existing with
            | Some value when sameImmutable draft value -> return Ok(RecoveryRetain.Existing value)
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                match PreparationIntegrity.validateDraft draft with
                | Error failure -> return Error failure
                | Ok() ->
                    let! count, bytes = readCapacity connection transaction

                    if
                        count >= int64 limits.MaximumPreparations
                        || bytes + int64 draft.CanonicalRequest.Length > limits.MaximumCanonicalRequestBytes
                    then
                        return Error RecoveryStoreFailure.CapacityExceeded
                    else
                        let! inserted = insertPreparation connection transaction draft
                        return Ok(RecoveryRetain.Created inserted)
        }

/// PostgreSQL implementation of Application's private technical recovery port.
/// The composed runtime owns the shared data source; this store owns no pool or public service seam.
type internal PostgresRecoveryStore(dataSource: NpgsqlDataSource, limits: PreparationLimits) =
    do PreparationLimits.validate limits

    interface IRecoveryStore with
        member _.InstallationLineage cancellationToken =
            RecoveryStoreQueries.installationLineage dataSource cancellationToken

        member _.Retain(draft, cancellationToken) =
            task {
                match PreparationIntegrity.validateIdentity draft with
                | Error failure -> return Error failure
                | Ok _ ->
                    let commitStarted = ref false

                    try
                        use! connection = RuntimeDatabase.openConnectionAsync dataSource

                        let! result =
                            withTransaction
                                connection
                                cancellationToken
                                commitStarted
                                (fun transaction ->
                                    RecoveryMutationLogic.retain
                                        connection
                                        transaction
                                        limits
                                        draft)

                        return result
                    with error ->
                        return Error(mutationFailure commitStarted.Value error)
            }

        member _.Get(operationId, cancellationToken) =
            task {
                if operationId = Guid.Empty then
                    return Error(RecoveryStoreFailure.InvalidInput "operationId")
                else
                    try
                        cancellationToken.ThrowIfCancellationRequested()
                        use! connection = RuntimeDatabase.openConnectionAsync dataSource
                        let! preparation = readOne connection None operationId
                        cancellationToken.ThrowIfCancellationRequested()
                        return Ok preparation
                    with
                    | :? OperationCanceledException ->
                        return Error RecoveryStoreFailure.ReadCancelled
                    | error -> return Error(fail error)
            }

        member _.List(after, pageSize, cancellationToken) =
            task {
                if pageSize < 1 || pageSize > limits.MaximumPageSize then
                    return Error(RecoveryStoreFailure.InvalidInput "pageSize")
                else
                    try
                        cancellationToken.ThrowIfCancellationRequested()
                        use! connection = RuntimeDatabase.openConnectionAsync dataSource
                        let! page = list connection after pageSize
                        cancellationToken.ThrowIfCancellationRequested()
                        return Ok page
                    with
                    | :? OperationCanceledException ->
                        return Error RecoveryStoreFailure.ReadCancelled
                    | error -> return Error(fail error)
            }

        member _.Start(operationId, cancellationToken) =
            task {
                if operationId = Guid.Empty then
                    return Error(RecoveryStoreFailure.InvalidInput "operationId")
                else
                    let commitStarted = ref false

                    try
                        use! connection = RuntimeDatabase.openConnectionAsync dataSource

                        let! result =
                            withTransaction
                                connection
                                cancellationToken
                                commitStarted
                                (fun transaction ->
                                    task {
                                        do!
                                            Sql.lockKeyAsync
                                                connection
                                                transaction
                                                ("claimcore:request-preparation:"
                                                 + operationId.ToString("D"))

                                        let! existing =
                                            readOne connection (Some transaction) operationId

                                        match existing with
                                        | None -> return Error RecoveryStoreFailure.NotFound
                                        | Some preparation ->
                                            let! outcome =
                                                start
                                                    connection
                                                    transaction
                                                    operationId
                                                    preparation

                                            return Ok outcome
                                    })

                        return result
                    with error ->
                        return Error(mutationFailure commitStarted.Value error)
            }

        member _.Settle(attemptId, outcome, cancellationToken) =
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
                                (fun transaction -> settle connection transaction attemptId outcome)
                    with error ->
                        return Error(mutationFailure commitStarted.Value error)
            }

        member _.Dismiss(operationId, cancellationToken) =
            task {
                if operationId = Guid.Empty then
                    return Error(RecoveryStoreFailure.InvalidInput "operationId")
                else
                    let commitStarted = ref false

                    try
                        use! connection = RuntimeDatabase.openConnectionAsync dataSource

                        let! result =
                            withTransaction
                                connection
                                cancellationToken
                                commitStarted
                                (fun transaction ->
                                    task {
                                        do!
                                            Sql.lockKeyAsync
                                                connection
                                                transaction
                                                ("claimcore:request-preparation:"
                                                 + operationId.ToString("D"))

                                        let! existing =
                                            readOne connection (Some transaction) operationId

                                        match existing with
                                        | None -> return Error RecoveryStoreFailure.NotFound
                                        | Some preparation ->
                                            let! outcome =
                                                dismiss
                                                    connection
                                                    transaction
                                                    operationId
                                                    preparation

                                            return Ok outcome
                                    })

                        return result
                    with error ->
                        return Error(mutationFailure commitStarted.Value error)
            }
