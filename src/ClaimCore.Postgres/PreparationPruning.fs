namespace ClaimCore.Postgres

open System.Data
open System.IO
open Npgsql
open NpgsqlTypes

/// Owner-only pruning of accepted or durably revoked technical preparations. A rejected attempt is
/// evidence, not authority closure: it can become valid after later case or business-date changes.
module PreparationPruning =
    let private candidatesSql =
        """
        WITH candidates AS (
            SELECT p.operation_id
            FROM claimcore.request_preparations p
            LEFT JOIN claimcore.case_changes c ON c.operation_id = p.operation_id
            WHERE (
                (c.operation_id IS NOT NULL AND c.recorded_at < clock_timestamp() - make_interval(days => @settled))
                OR (c.operation_id IS NULL AND EXISTS (
                    SELECT 1
                    FROM claimcore.operation_revocations r
                    WHERE r.operation_id = p.operation_id
                        AND r.revoked_at < clock_timestamp() - make_interval(days => @abandoned)
                )))
                AND NOT EXISTS (
                    SELECT 1 FROM claimcore.request_submission_attempts a
                    LEFT JOIN claimcore.request_submission_settlements s ON s.attempt_id = a.attempt_id
                    WHERE a.operation_id = p.operation_id AND s.attempt_id IS NULL
                )
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
            AdministrationFailures.refuse AdministrationFailure.PruneAuditFailed

    let private terminalFootprint connection transaction =
        use command =
            new NpgsqlCommand(
                "SELECT count(*), COALESCE(sum(octet_length(p.canonical_request)), 0)::bigint "
                + "FROM claimcore.request_preparations p "
                + "WHERE EXISTS (SELECT 1 FROM claimcore.case_changes c "
                + "              WHERE c.operation_id = p.operation_id) "
                + "OR EXISTS (SELECT 1 FROM claimcore.operation_revocations r "
                + "           WHERE r.operation_id = p.operation_id)",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            AdministrationFailures.refuse AdministrationFailure.RecoveryFootprintUnreadable

        reader.GetInt64(0), reader.GetInt64(1)

    let private pruneValue
        (progress: AdministrationProgress<PreparationPruneResult>)
        connectionString
        (options: PreparationPruneOptions)
        =
        PreparationPruneOptions.validate options
        let builder = OwnerConnection.builder connectionString
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        DatabaseEnvironment.requireCompatible connection
        OwnerConnection.requireIdentity connection
        SchemaBaseline.requireCurrent connection
        use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
        progress.BeginWork()
        Sql.lockKey connection transaction "claimcore:request-preparation-prune"
        let candidateCount = candidates connection transaction options

        let deletedCount =
            if options.DryRun then
                0
            else
                delete connection transaction options

        record connection transaction options candidateCount deletedCount
        let terminalCount, terminalBytes = terminalFootprint connection transaction

        let result =
            {
                CandidateCount = candidateCount
                DeletedCount = deletedCount
                DryRun = options.DryRun
                TerminalPreparationCount = terminalCount
                TerminalCanonicalRequestBytes = terminalBytes
            }

        progress.Commit((fun () -> transaction.Commit()), result)

    let prune connectionString options =
        AdministrationExecution.run (fun progress -> pruneValue progress connectionString options)
