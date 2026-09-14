namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OperationAuthorityStore
open PreparationData
open RecoveryExecutionSupport

/// The one storage-owned transaction that rechecks authority, invokes the pure Domain callback,
/// persists accepted state, and records a definite settlement together.
module internal RecoveryExecutionStore =
    let private acceptExisting
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (attemptId: Guid)
        receipt
        cancellationToken
        commitStarted
        =
        task {
            do!
                settleExistingAttempt
                    connection
                    transaction
                    request.OperationId
                    attemptId
                    RecoverySettlement.Accepted

            do! commit transaction cancellationToken commitStarted
            return Ok(AdmittedExecution.Accepted receipt)
        }

    let private revokeExisting
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (attemptId: Guid)
        cancellationToken
        commitStarted
        =
        task {
            do!
                settleExistingAttempt
                    connection
                    transaction
                    request.OperationId
                    attemptId
                    RecoverySettlement.RevokedBeforeExecution

            do! commit transaction cancellationToken commitStarted

            return Ok(AdmittedExecution.RevokedBeforeExecution SettlementConfirmation.Confirmed)
        }

    let private rejectPending
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (attemptId: Guid)
        rejection
        cancellationToken
        commitStarted
        (knownRejection: DomainError option ref)
        =
        task {
            knownRejection.Value <- Some rejection
            do! settleRequired connection transaction attemptId RecoverySettlement.Rejected
            do! commit transaction cancellationToken commitStarted
            return Ok(AdmittedExecution.Rejected(rejection, SettlementConfirmation.Confirmed))
        }

    let private acceptPending
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (fingerprint: string)
        current
        claim
        (attemptId: Guid)
        cancellationToken
        commitStarted
        =
        task {
            let! receipt =
                StoreTransaction.persistUnderCaseLock
                    connection
                    transaction
                    request
                    fingerprint
                    current
                    claim

            do! settleRequired connection transaction attemptId RecoverySettlement.Accepted
            do! commit transaction cancellationToken commitStarted
            return Ok(AdmittedExecution.Accepted receipt)
        }

    let private executePending
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (fingerprint: string)
        (attemptId: Guid)
        (today: unit -> DateOnly)
        decide
        (cancellationToken: Threading.CancellationToken)
        (commitStarted: bool ref)
        (knownRejection: DomainError option ref)
        =
        task {
            let! preparation = readHeader connection (Some transaction) request.OperationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some retained when retained.RequestSha256 <> fingerprint ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some _ ->
                let! admitted = attemptExists connection transaction request.OperationId attemptId

                if not admitted then
                    return Error RecoveryStoreFailure.NotFound
                else
                    do! Sql.lockKeyAsync connection transaction ("case:" + request.CaseReference)
                    let! current = StoreData.readCase connection transaction request.CaseReference

                    match decide (today ()) current with
                    | Error rejection ->
                        return!
                            rejectPending
                                connection
                                transaction
                                attemptId
                                rejection
                                cancellationToken
                                commitStarted
                                knownRejection
                    | Ok claim ->
                        return!
                            acceptPending
                                connection
                                transaction
                                request
                                fingerprint
                                current
                                claim
                                attemptId
                                cancellationToken
                                commitStarted
        }

    let private executeWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (fingerprint: string)
        (attemptId: Guid)
        (today: unit -> DateOnly)
        decide
        (cancellationToken: Threading.CancellationToken)
        (commitStarted: bool ref)
        (knownRejection: DomainError option ref)
        (knownRevocation: bool ref)
        =
        task {
            let! revoked = find connection transaction request.OperationId

            match revoked with
            | Some value when matches fingerprint value ->
                knownRevocation.Value <- true

                return!
                    revokeExisting
                        connection
                        transaction
                        request
                        attemptId
                        cancellationToken
                        commitStarted
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    executePending
                        connection
                        transaction
                        request
                        fingerprint
                        attemptId
                        today
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
        }

    let private executeInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operation: PreparedOperation)
        (attemptId: Guid)
        (today: unit -> DateOnly)
        decide
        (cancellationToken: Threading.CancellationToken)
        (commitStarted: bool ref)
        (knownRejection: DomainError option ref)
        (knownRevocation: bool ref)
        =
        task {
            let request = Operation.request operation
            let fingerprint = Operation.fingerprint operation
            do! Sql.lockKeyAsync connection transaction (operationKey request.OperationId)

            let! accepted =
                StoreData.readOperation connection (Some transaction) request.OperationId

            match accepted with
            | Some(receipt, original) when original = fingerprint ->
                return!
                    acceptExisting
                        connection
                        transaction
                        request
                        attemptId
                        receipt
                        cancellationToken
                        commitStarted
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    executeWithoutAccepted
                        connection
                        transaction
                        request
                        fingerprint
                        attemptId
                        today
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
                        knownRevocation
        }

    let executeAdmitted
        (dataSource: NpgsqlDataSource)
        operation
        (attemptId: Guid)
        (today: unit -> DateOnly)
        decide
        (cancellationToken: Threading.CancellationToken)
        : Task<Result<AdmittedExecution, RecoveryStoreFailure>> =
        task {
            let request = Operation.request operation
            let commitStarted = ref false
            let knownRejection = ref None
            let knownRevocation = ref false

            try
                cancellationToken.ThrowIfCancellationRequested()
                use! connection = RuntimeDatabase.openConnectionAsync dataSource

                let! transaction =
                    connection.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        cancellationToken
                    )

                use _ = transaction

                try
                    return!
                        executeInTransaction
                            connection
                            transaction
                            operation
                            attemptId
                            today
                            decide
                            cancellationToken
                            commitStarted
                            knownRejection
                            knownRevocation
                with error ->
                    do! rollback transaction

                    return
                        unconfirmedOutcome
                            request
                            knownRejection
                            knownRevocation
                            commitStarted
                            error
            with error ->
                return Error(mutationFailure commitStarted.Value error)
        }
