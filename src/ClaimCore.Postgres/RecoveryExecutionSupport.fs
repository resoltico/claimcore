namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open PreparationData
open SubmissionAttemptStore

/// Shared transaction primitives for recovery execution. They keep commit knowledge and settlement
/// requirements explicit while the store module owns operation/case locking and persistence choices.
module internal RecoveryExecutionSupport =
    let operationKey (operationId: Guid) =
        "operation:" + operationId.ToString("D")

    let attemptExists
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (attemptId: Guid)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.request_submission_attempts "
                    + "WHERE operation_id = @operation AND attempt_id = @attempt)",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            Sql.uuid command "attempt" attemptId
            let! value = command.ExecuteScalarAsync()
            return value :?> bool
        }

    let settleRequired connection transaction attemptId outcome =
        task {
            match! settle connection transaction attemptId outcome with
            | Ok() -> return ()
            | Error _ ->
                return
                    raise (
                        System.IO.InvalidDataException(
                            "A required submission settlement is absent."
                        )
                    )
        }

    let commit
        (transaction: NpgsqlTransaction)
        (cancellationToken: Threading.CancellationToken)
        (commitStarted: bool ref)
        =
        commitBoundary cancellationToken commitStarted (fun () ->
            transaction.CommitAsync(Threading.CancellationToken.None))

    let settleExistingAttempt
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (attemptId: Guid)
        outcome
        =
        task {
            let! retainedAttempt = attemptExists connection transaction operationId attemptId

            if retainedAttempt then
                do! settleRequired connection transaction attemptId outcome
        }

    let rollback (transaction: NpgsqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync(Threading.CancellationToken.None)
            with _ ->
                ()
        }

    let unconfirmedOutcome
        (request: CommandRequest)
        (knownRejection: DomainError option ref)
        (knownRevocation: bool ref)
        (commitStarted: bool ref)
        (error: exn)
        =
        match knownRejection.Value, knownRevocation.Value, commitStarted.Value with
        | Some rejection, _, _ ->
            Ok(AdmittedExecution.Rejected(rejection, SettlementConfirmation.Unconfirmed))
        | None, true, _ ->
            Ok(AdmittedExecution.RevokedBeforeExecution SettlementConfirmation.Unconfirmed)
        | None, false, true -> Ok(AdmittedExecution.CommitOutcomeUnknown request.OperationId)
        | None, false, false ->
            match error with
            | :? OperationCanceledException -> Error RecoveryStoreFailure.CancelledBeforeCommit
            | _ ->
                Ok(
                    AdmittedExecution.FailedBeforeCommit(
                        StoreData.failure false request.OperationId error,
                        SettlementConfirmation.Unconfirmed
                    )
                )
