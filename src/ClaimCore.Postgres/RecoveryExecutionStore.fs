namespace ClaimCore.Postgres

open System.Threading
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
                RecoveryAcceptedObservation.read
                    connection
                    transaction
                    witness
                    request.OperationId
                    (Operation.fingerprint operation)
                    cancellationToken

            match accepted with
            | Ok(Some receipt) ->
                // A later accepted receipt cannot settle a different historical attempt.
                return Ok(AdmittedExecution.ObservedAccepted receipt)
            | Error failure -> return Error failure
            | Ok None ->
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

    let private authorizedCase
        connection
        transaction
        (actorContext: ActorCallContext)
        request
        revision
        =
        task {
            match actorContext.CaseId with
            | None -> return false
            | Some caseId ->
                return!
                    ActorMutationGuard.authorize
                        connection
                        transaction
                        actorContext
                        request
                        caseId
                        revision
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

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    (operationKey request.OperationId)
                    cancellationToken

            let! allowed = authorizedCase connection transaction actorContext request revision

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
                    CancellationToken.None

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

                do! active.Admit(cancellationToken)

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
