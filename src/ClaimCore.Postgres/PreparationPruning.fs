namespace ClaimCore.Postgres

open System.Data
open System.IO
open Npgsql
open NpgsqlTypes

/// Owner-only pruning of accepted, fully settled, or explicitly dismissed technical preparations.
module PreparationPruning =
    let private candidatesSql =
        """
        WITH candidates AS (
            SELECT p.operation_id
            FROM claimcore.request_preparations p
            LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id
            LEFT JOIN claimcore.case_changes c ON c.operation_id = p.operation_id
            WHERE
                (c.operation_id IS NOT NULL AND c.recorded_at < clock_timestamp() - make_interval(days => @settled))
                OR (c.operation_id IS NULL AND l.state = 'DISMISSED'
                    AND l.recorded_at < clock_timestamp() - make_interval(days => @abandoned))
                OR (c.operation_id IS NULL AND l.state = 'SUBMISSION_STARTED'
                    AND NOT EXISTS (
                        SELECT 1 FROM claimcore.request_submission_legacy_uncertainty u
                        WHERE u.operation_id = p.operation_id
                    )
                    AND EXISTS (
                        SELECT 1 FROM claimcore.request_submission_attempts a
                        WHERE a.operation_id = p.operation_id
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM claimcore.request_submission_attempts a
                        LEFT JOIN claimcore.request_submission_settlements s
                            ON s.attempt_id = a.attempt_id
                        WHERE a.operation_id = p.operation_id AND s.attempt_id IS NULL
                    )
                    AND (
                        SELECT max(s.recorded_at)
                        FROM claimcore.request_submission_attempts a
                        JOIN claimcore.request_submission_settlements s
                            ON s.attempt_id = a.attempt_id
                        WHERE a.operation_id = p.operation_id
                    ) < clock_timestamp() - make_interval(days => @settled))
            ORDER BY p.prepared_at, p.operation_id
            LIMIT @limit
            FOR UPDATE OF p
        )
        """

    let private bind (command: NpgsqlCommand) (options: PreparationPruneOptions) =
        command.Parameters.AddWithValue("settled", options.SettledRetentionDays)
        |> ignore

        command.Parameters.AddWithValue("abandoned", options.AbandonedRetentionDays)
        |> ignore

        command.Parameters.AddWithValue("limit", options.BatchLimit) |> ignore

    let private candidates connection transaction options =
        use command =
            new NpgsqlCommand(
                candidatesSql + "SELECT count(*) FROM candidates",
                connection,
                transaction
            )

        bind command options
        command.ExecuteScalar() :?> int64 |> int

    let private delete connection transaction options =
        use command =
            new NpgsqlCommand(
                candidatesSql
                + "DELETE FROM claimcore.request_preparations p USING candidates c WHERE p.operation_id = c.operation_id",
                connection,
                transaction
            )

        bind command options
        command.ExecuteNonQuery()

    let private record
        connection
        transaction
        (options: PreparationPruneOptions)
        candidates
        deleted
        =
        use command =
            new NpgsqlCommand(
                """INSERT INTO claimcore.request_preparation_prunes (
                       dry_run, settled_retention_days, abandoned_retention_days, batch_limit,
                       candidate_count, deleted_count
                   ) VALUES (@dryRun, @settled, @abandoned, @limit, @candidates, @deleted)""",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("dryRun", options.DryRun) |> ignore
        bind command options
        command.Parameters.AddWithValue("candidates", candidates) |> ignore
        command.Parameters.AddWithValue("deleted", deleted) |> ignore

        if command.ExecuteNonQuery() <> 1 then
            raise (InvalidDataException("Preparation prune audit was not recorded."))

    let prune connectionString (options: PreparationPruneOptions) =
        PreparationPruneOptions.validate options
        let builder = Migrations.ownerBuilder connectionString
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        DatabaseEnvironment.requireCompatible connection
        Migrations.requireOwnerIdentity connection
        Migrations.requireCurrent connection
        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
        Sql.lockKey connection transaction "claimcore:request-preparation-prune"
        let candidateCount = candidates connection transaction options

        let deletedCount =
            if options.DryRun then
                0
            else
                delete connection transaction options

        record connection transaction options candidateCount deletedCount
        transaction.Commit()

        {
            CandidateCount = candidateCount
            DeletedCount = deletedCount
            DryRun = options.DryRun
        }
