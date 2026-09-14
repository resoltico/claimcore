namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open PreparationLifecycleStore

module internal SubmissionAttemptStore =
    let private outcomeToken outcome =
        match outcome with
        | RecoverySettlement.Accepted -> "ACCEPTED"
        | RecoverySettlement.Rejected -> "REJECTED"
        | RecoverySettlement.FailedBeforeCommit -> "ERROR"
        | RecoverySettlement.RevokedBeforeExecution -> "REVOKED_BEFORE_EXECUTION"

    let private insertAttempt connection transaction operationId =
        task {
            let attemptId = Guid.NewGuid()

            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.request_submission_attempts (attempt_id, operation_id) VALUES (@attempt, @operation)",
                    connection,
                    transaction
                )

            Sql.uuid command "attempt" attemptId
            Sql.uuid command "operation" operationId
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Submission attempt was not recorded."))

            return attemptId
        }

    let private attemptCount connection transaction operationId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.request_submission_attempts WHERE operation_id = @operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! value = command.ExecuteScalarAsync()
            return value :?> int64
        }

    let start
        maximumAttempts
        connection
        transaction
        operationId
        preparation
        : Task<Result<RecoveryStart, RecoveryStoreFailure>> =
        task {
            let! lifecycle = admitSubmission connection transaction operationId preparation

            match lifecycle with
            | SubmissionLifecycle.Dismissed retained -> return Ok(RecoveryStart.Dismissed retained)
            | SubmissionLifecycle.Started retained ->
                let! count = attemptCount connection transaction operationId

                if count >= int64 maximumAttempts then
                    return Error RecoveryStoreFailure.CapacityExceeded
                else
                    let! attemptId = insertAttempt connection transaction operationId
                    return Ok(RecoveryStart.Started(attemptId, retained))
            | SubmissionLifecycle.AlreadyStarted retained ->
                let! count = attemptCount connection transaction operationId

                if count >= int64 maximumAttempts then
                    return Error RecoveryStoreFailure.CapacityExceeded
                else
                    let! attemptId = insertAttempt connection transaction operationId
                    return Ok(RecoveryStart.AlreadyStarted(attemptId, retained))
        }

    let settle connection transaction attemptId outcome =
        task {
            let token = outcomeToken outcome

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.request_submission_settlements (attempt_id, outcome) "
                    + "SELECT @attempt, @outcome WHERE EXISTS (SELECT 1 FROM claimcore.request_submission_attempts WHERE attempt_id = @attempt) "
                    + "ON CONFLICT (attempt_id) DO NOTHING",
                    connection,
                    transaction
                )

            Sql.uuid insert "attempt" attemptId
            Sql.text insert "outcome" token
            let! inserted = insert.ExecuteNonQueryAsync()

            if inserted = 1 then
                return Ok()
            else
                use inspect =
                    new NpgsqlCommand(
                        "SELECT s.outcome FROM claimcore.request_submission_attempts a "
                        + "LEFT JOIN claimcore.request_submission_settlements s ON s.attempt_id = a.attempt_id "
                        + "WHERE a.attempt_id = @attempt",
                        connection,
                        transaction
                    )

                Sql.uuid inspect "attempt" attemptId
                let! result = inspect.ExecuteReaderAsync()
                use reader = result
                let! exists = reader.ReadAsync()

                if not exists then
                    return Error RecoveryStoreFailure.NotFound
                elif reader.IsDBNull(0) then
                    return raise (InvalidDataException("Submission settlement was not recorded."))
                elif reader.GetString(0) = token then
                    return Ok()
                else
                    return Error RecoveryStoreFailure.IdempotencyConflict
        }
