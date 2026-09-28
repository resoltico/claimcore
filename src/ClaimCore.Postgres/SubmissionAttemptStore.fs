namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open PreparationLifecycleStore

module internal SubmissionAttemptStore =
    let private outcomeToken outcome =
        match outcome with
        | RecoverySettlement.Accepted -> "ACCEPTED"
        | RecoverySettlement.Rejected -> "REJECTED"
        | RecoverySettlement.FailedBeforeCommit -> "ERROR"
        | RecoverySettlement.RevokedBeforeExecution -> "REVOKED_BEFORE_EXECUTION"

    let private insertAttempt
        connection
        transaction
        operationId
        (actorContext: ActorCallContext)
        attemptId
        ordinal
        (intent: WitnessIntent)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.request_submission_attempts "
                    + "(attempt_id, attempt_ordinal, witness_event_id, witness_sequence, "
                    + "witness_epoch, witness_entry_hash, witness_candidate_sha256, "
                    + "operation_id, submitter_actor_id, resolver_actor_id, grant_revision) "
                    + "VALUES (@attempt, @ordinal, @event, @sequence, @epoch, @hash, "
                    + "@candidateHash, @operation, @submitter, @resolver, @grantRevision)",
                    connection,
                    transaction
                )

            Sql.uuid command "attempt" attemptId
            Sql.integer command "ordinal" ordinal
            Sql.uuid command "event" attemptId
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "hash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.add command "candidateHash" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.uuid command "operation" operationId

            let submitter, resolver =
                if actorContext.Action = EndpointAction.RecoveryResolve then
                    None, Some actorContext.Binding.ActorId
                else
                    Some actorContext.Binding.ActorId, None

            Sql.optional command "submitter" NpgsqlDbType.Uuid submitter
            Sql.optional command "resolver" NpgsqlDbType.Uuid resolver
            Sql.integer command "grantRevision" actorContext.Binding.GrantRevision
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Submission attempt was not recorded."))

            return attemptId
        }

    let private attemptCount connection transaction operationId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*),COALESCE(max(attempt_ordinal),0)::bigint "
                    + "FROM claimcore.request_submission_attempts WHERE operation_id = @operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()

            if not exists then
                raise (InvalidDataException("Attempt count is unavailable."))

            let count = reader.GetInt64(0)
            let maximum = reader.GetInt64(1)

            if count <> maximum then
                raise (InvalidDataException("Attempt ordinal gap exists."))

            return count
        }

    let private persistStarted
        connection
        transaction
        operationId
        retained
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        ordinal
        wasPreviouslyStarted
        =
        task {
            let actorId = actorContext.Binding.ActorId

            let role =
                if actorContext.Action = EndpointAction.RecoveryResolve then
                    "RESOLVER"
                else
                    "SUBMITTER"

            let attemptId, intent =
                WitnessTechnical.beginStart
                    witness
                    retained
                    ordinal
                    actorId
                    role
                    actorContext.Binding.GrantRevision

            pending.Value <- Some(attemptId, intent)

            let! inserted =
                insertAttempt
                    connection
                    transaction
                    operationId
                    actorContext
                    attemptId
                    ordinal
                    intent

            return
                if wasPreviouslyStarted then
                    Ok(RecoveryStart.AlreadyStarted(inserted, retained))
                else
                    Ok(RecoveryStart.Started(inserted, retained))
        }

    let private startAfterCapacity
        connection
        transaction
        operationId
        preparation
        actorContext
        witness
        pending
        count
        =
        task {
            let! lifecycle = admitSubmission connection transaction operationId preparation

            match lifecycle with
            | SubmissionLifecycle.Dismissed retained -> return Ok(RecoveryStart.Dismissed retained)
            | SubmissionLifecycle.Started retained ->
                return!
                    persistStarted
                        connection
                        transaction
                        operationId
                        retained
                        actorContext
                        witness
                        pending
                        (count + 1L)
                        false
            | SubmissionLifecycle.AlreadyStarted retained ->
                return!
                    persistStarted
                        connection
                        transaction
                        operationId
                        retained
                        actorContext
                        witness
                        pending
                        (count + 1L)
                        true
        }

    let start
        maximumAttempts
        connection
        transaction
        operationId
        preparation
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        : Task<Result<RecoveryStart, RecoveryStoreFailure>> =
        task {
            let recovered =
                WitnessTechnicalStartReconcile.reconcileLatestStart
                    witness
                    connection
                    transaction
                    preparation

            let requestedRole =
                if actorContext.Action = EndpointAction.RecoveryResolve then
                    "RESOLVER"
                else
                    "SUBMITTER"

            match recovered with
            | Some(attemptId, actorId, role) when
                actorId = actorContext.Binding.ActorId && role = requestedRole
                ->
                return Ok(RecoveryStart.AlreadyStarted(attemptId, preparation))
            | _ ->
                let! count = attemptCount connection transaction operationId

                if count >= int64 maximumAttempts then
                    return Error RecoveryStoreFailure.CapacityExceeded
                else
                    return!
                        startAfterCapacity
                            connection
                            transaction
                            operationId
                            preparation
                            actorContext
                            witness
                            pending
                            count
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
