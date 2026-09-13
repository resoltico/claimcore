namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application

/// Reads append-only technical attempt evidence separately from the immutable preparation row.
/// Only detailed recovery reads project this evidence; list summaries stay bounded.
module internal PreparationEvidence =
    let private attempts
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (operationId: Guid)
        : Task<PreparationAttempt list> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.attempt_id, a.started_at, s.outcome, s.recorded_at "
                    + "FROM claimcore.request_submission_attempts a "
                    + "LEFT JOIN claimcore.request_submission_settlements s ON s.attempt_id = a.attempt_id "
                    + "WHERE a.operation_id = @operation ORDER BY a.started_at, a.attempt_id",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let found = ResizeArray<PreparationAttempt>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync()
                reading <- hasRow

                if hasRow then
                    found.Add(
                        {
                            AttemptId = reader.GetGuid(0)
                            StartedAt = reader.GetFieldValue<DateTimeOffset>(1)
                            Settlement =
                                if reader.IsDBNull(2) then
                                    None
                                else
                                    Some(reader.GetString(2))
                            SettledAt =
                                if reader.IsDBNull(3) then
                                    None
                                else
                                    Some(reader.GetFieldValue<DateTimeOffset>(3))
                        }
                    )

            return List.ofSeq found
        }

    let private legacyMarker
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (operationId: Guid)
        : Task<bool> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.request_submission_legacy_uncertainty WHERE operation_id = @operation)",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            Sql.uuid command "operation" operationId
            let! value = command.ExecuteScalarAsync()
            return value :?> bool
        }

    let read connection transaction operationId =
        task {
            let! attemptEvidence = attempts connection transaction operationId
            let! legacyUncertainty = legacyMarker connection transaction operationId
            return attemptEvidence, legacyUncertainty
        }
