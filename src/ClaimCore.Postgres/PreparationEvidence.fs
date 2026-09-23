namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Reads append-only technical attempt evidence separately from the immutable preparation row.
/// Only detailed recovery reads project this evidence; list summaries stay bounded.
module internal PreparationEvidence =
    let private attempt (reader: DbDataReader) =
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

    let private collectAttempts (reader: DbDataReader) =
        task {
            let found = ResizeArray<PreparationAttempt>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync()
                reading <- hasRow

                if hasRow then
                    found.Add(attempt reader)

            return List.ofSeq found
        }

    let private attempts
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (operationId: Guid)
        (after: RecoveryAttemptCursor option)
        limit
        : Task<PreparationAttempt list> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.attempt_id, a.started_at, s.outcome, s.recorded_at "
                    + "FROM claimcore.request_submission_attempts a "
                    + "LEFT JOIN claimcore.request_submission_settlements s ON s.attempt_id = a.attempt_id "
                    + "WHERE a.operation_id = @operation "
                    + "AND (@afterStartedAt IS NULL OR a.started_at > @afterStartedAt "
                    + "OR (a.started_at = @afterStartedAt AND a.attempt_id > @afterAttempt)) "
                    + "ORDER BY a.started_at, a.attempt_id LIMIT @limit",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            Sql.uuid command "operation" operationId

            Sql.optional
                command
                "afterStartedAt"
                NpgsqlDbType.TimestampTz
                (after |> Option.map _.StartedAt)

            Sql.optional command "afterAttempt" NpgsqlDbType.Uuid (after |> Option.map _.AttemptId)

            let rowLimit = command.Parameters.Add("limit", NpgsqlDbType.Integer)
            rowLimit.Value <- limit + 1
            let! result = command.ExecuteReaderAsync()
            use reader = result
            return! collectAttempts reader
        }

    let readPage connection transaction operationId after limit : Task<RecoveryAttemptPage> =
        task {
            if
                after
                |> Option.exists (fun (value: RecoveryAttemptCursor) ->
                    value.OperationId <> operationId)
            then
                return
                    raise (
                        InvalidDataException(
                            "Recovery attempt cursor belongs to another operation."
                        )
                    )
            else
                let! attemptEvidence = attempts connection transaction operationId after limit
                let items = attemptEvidence |> List.truncate limit

                let page: RecoveryAttemptPage =
                    {
                        Items = items
                        NextAfter =
                            if attemptEvidence.Length > items.Length then
                                items
                                |> List.tryLast
                                |> Option.map (fun item ->
                                    {
                                        OperationId = operationId
                                        StartedAt = item.StartedAt
                                        AttemptId = item.AttemptId
                                    })
                            else
                                None
                    }

                return page
        }
