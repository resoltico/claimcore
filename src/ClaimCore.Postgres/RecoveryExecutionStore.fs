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
open RecoveryExecutionOutcomes
open RecoveryExecutionDecision

/// Owns the operation/case lock and dispatches to witnessed outcomes.
module internal RecoveryExecutionStore =
    let private completeExisting
        connection
        transaction
        request
        attemptId
        receipt
        cancellationToken
        commitStarted
        witness
        =
        acceptExisting
            connection
            transaction
            request
            attemptId
            receipt
            cancellationToken
            commitStarted
            witness

    let private executeKnown
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operation: PreparedOperation)
        (attemptId: Guid)
        (capture: unit -> BusinessContext)
        decide
        (cancellationToken: Threading.CancellationToken)
        (commitStarted: bool ref)
        (knownRejection: DomainError option ref)
        (knownRevocation: bool ref)
        (actorContext: ActorCallContext)
        revision
        (witness: WitnessProtocol)
        =
        task {
            let request = Operation.request operation

            let! accepted =
                StoreData.readOperation connection (Some transaction) request.OperationId

            match accepted with
            | Some(receipt, original) when original = Operation.fingerprint operation ->
                return!
                    completeExisting
                        connection
                        transaction
                        request
                        attemptId
                        receipt
                        cancellationToken
                        commitStarted
                        witness
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    executeWithoutAccepted
                        connection
                        transaction
                        operation
                        (Operation.fingerprint operation)
                        attemptId
                        capture
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
                        knownRevocation
                        actorContext
                        revision
                        witness
        }

    let private executeInTransaction
        connection
        transaction
        (operation: PreparedOperation)
        attemptId
        capture
        decide
        cancellationToken
        commitStarted
        knownRejection
        knownRevocation
        (actorContext: ActorCallContext)
        revision
        witness
        =
        task {
            let request = Operation.request operation
            do! Sql.lockKeyAsync connection transaction (operationKey request.OperationId)

            match actorContext.CaseId with
            | None -> return Error RecoveryStoreFailure.ResourceUnavailable
            | Some caseId ->
                let! allowed =
                    ActorMutationGuard.authorize
                        connection
                        transaction
                        actorContext
                        request
                        caseId
                        revision

                if not allowed then
                    return Error RecoveryStoreFailure.ResourceUnavailable
                else
                    return!
                        executeKnown
                            connection
                            transaction
                            operation
                            attemptId
                            capture
                            decide
                            cancellationToken
                            commitStarted
                            knownRejection
                            knownRevocation
                            actorContext
                            revision
                            witness
        }

    let private executeWithTransaction
        (dataSource: NpgsqlDataSource)
        operation
        (attemptId: Guid)
        (capture: unit -> BusinessContext)
        decide
        (cancellationToken: Threading.CancellationToken)
        (active: WitnessProtocol)
        (commitStarted: bool ref)
        (knownRejection: DomainError option ref)
        (knownRevocation: bool ref)
        (actorContext: ActorCallContext)
        : Task<Result<AdmittedExecution, RecoveryStoreFailure>> =
        task {
            let request = Operation.request operation
            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            use! _authorityLease =
                AuthorityOperationFence.acquireShared
                    (Some dataSource)
                    connection
                    System.Threading.CancellationToken.None

            let! transaction =
                connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)

            use _ = transaction

            try
                let! revision =
                    ActorGrantRead.lockRevision connection transaction true cancellationToken

                return!
                    executeInTransaction
                        connection
                        transaction
                        operation
                        attemptId
                        capture
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
                        knownRevocation
                        actorContext
                        revision
                        active
            with error ->
                do! rollback transaction
                return unconfirmedOutcome request knownRejection knownRevocation commitStarted error
        }

    let executeAdmitted
        (dataSource: NpgsqlDataSource)
        operation
        (attemptId: Guid)
        (capture: unit -> BusinessContext)
        decide
        (cancellationToken: Threading.CancellationToken)
        (witness: WitnessProtocol option)
        (actorContext: ActorCallContext option)
        : Task<Result<AdmittedExecution, RecoveryStoreFailure>> =
        task {
            let commitStarted = ref false
            let knownRejection = ref None
            let knownRevocation = ref false

            try
                cancellationToken.ThrowIfCancellationRequested()

                let active =
                    witness
                    |> Option.defaultWith (fun () -> invalidOp "Witness is required for mutation.")

                active.Admit()

                let actor =
                    actorContext
                    |> Option.defaultWith (fun () -> invalidOp "Actor is required for mutation.")

                return!
                    executeWithTransaction
                        dataSource
                        operation
                        attemptId
                        capture
                        decide
                        cancellationToken
                        active
                        commitStarted
                        knownRejection
                        knownRevocation
                        actor
            with error ->
                return Error(mutationFailure commitStarted.Value error)
        }
