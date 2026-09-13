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

    let start connection transaction operationId preparation =
        task {
            let! lifecycle = admitSubmission connection transaction operationId preparation

            match lifecycle with
            | SubmissionLifecycle.Dismissed retained -> return RecoveryStart.Dismissed retained
            | SubmissionLifecycle.Started retained ->
                let! attemptId = insertAttempt connection transaction operationId
                return RecoveryStart.Started(attemptId, retained)
            | SubmissionLifecycle.AlreadyStarted retained ->
                let! attemptId = insertAttempt connection transaction operationId
                return RecoveryStart.AlreadyStarted(attemptId, retained)
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
