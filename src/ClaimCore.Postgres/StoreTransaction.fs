namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Owns the PostgreSQL transaction protocol around the application's domain decision.
module internal StoreTransaction =
    let private commit (transaction: NpgsqlTransaction) (commitStarted: bool ref) receipt =
        task {
            commitStarted.Value <- true
            do! transaction.CommitAsync()
            return Ok receipt
        }

    /// Persist one Domain-approved transition while the caller already holds the case lock. The
    /// caller owns the surrounding operation authority and commit protocol.
    let persistUnderCaseLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        request
        fingerprint
        current
        claim
        =
        task {
            let snapshot = Claim.view claim

            let previous =
                current
                |> Option.map (fun value -> (Claim.view value).Version)
                |> Option.defaultValue 0L

            if
                snapshot.Fields.CaseReference <> request.CaseReference
                || snapshot.Version <> previous + 1L
            then
                raise (
                    InvalidDataException(
                        "The domain result did not satisfy the persistence contract."
                    )
                )

            let! receipt = StoreData.persist connection transaction request claim fingerprint
            return receipt
        }

    let private persistDecision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        request
        fingerprint
        current
        claim
        commitStarted
        =
        task {
            let! receipt =
                persistUnderCaseLock connection transaction request fingerprint current claim

            return! commit transaction commitStarted receipt
        }

    let private applyDecision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        request
        fingerprint
        decide
        commitStarted
        =
        task {
            do! Sql.lockKeyAsync connection transaction ("case:" + request.CaseReference)
            let! current = StoreData.readCase connection transaction request.CaseReference

            match decide current with
            | Error error -> return Error(CoreFailure.Domain error)
            | Ok claim ->
                return!
                    persistDecision
                        connection
                        transaction
                        request
                        fingerprint
                        current
                        claim
                        commitStarted
        }

    let private execute
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        decide
        commitStarted
        =
        task {
            let request = Operation.request operation

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + request.OperationId.ToString("D"))

            let fingerprint = Operation.fingerprint operation

            let! observed =
                StoreData.readOperation connection (Some transaction) request.OperationId

            match observed with
            | Some(receipt, original) when original = fingerprint -> return Ok receipt
            | Some _ -> return Error CoreFailure.IdempotencyConflict
            | None ->
                return!
                    applyDecision connection transaction request fingerprint decide commitStarted
        }

    let transact (dataSource: NpgsqlDataSource) operation decide =
        task {
            let request = Operation.request operation
            let commitStarted = ref false

            try
                use! connection = RuntimeDatabase.openConnectionAsync dataSource
                let! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted)
                use _ = transaction
                return! execute connection transaction operation decide commitStarted
            with
            | UnsupportedPostgresVersion
            | RuntimeDatabaseMismatch -> return Error CoreFailure.SchemaMismatch
            | :? NpgsqlException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? TimeoutException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? InvalidDataException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? InvalidCastException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
        }
