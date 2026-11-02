namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

/// Counts describe one statement snapshot; deletion additionally locks and rechecks each candidate.
type SessionPruneReport =
    {
        ObservedAt: DateTimeOffset
        Limit: int
        ExpiredAtObservation: int64
        Deleted: int64
    }

module SessionPruning =
    let MaximumBatch = 1000
    let DefaultBatch = 128

    let internal prune (source: NpgsqlDataSource) limit (token: CancellationToken) =
        if limit < 1 || limit > MaximumBatch then
            invalidArg (nameof limit) "Use a bounded positive pruning batch."

        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Baseline.require connection token
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! instant = SessionRows.now connection token

                    let sql =
                        "WITH candidates AS MATERIALIZED (SELECT session_key FROM claimcore.sessions WHERE "
                        + SessionRows.expiredPredicate
                        + " ORDER BY absolute_expiry,session_key LIMIT $2 FOR UPDATE SKIP LOCKED), deleted AS (DELETE FROM claimcore.sessions s USING candidates c WHERE s.session_key=c.session_key AND "
                        + SessionRows.expiredPredicate
                        + " RETURNING 1) SELECT (SELECT count(*) FROM claimcore.sessions WHERE "
                        + SessionRows.expiredPredicate
                        + "),(SELECT count(*) FROM deleted)"

                    let! rows =
                        DatabaseSession.rows connection sql [| box instant; box limit |] token

                    let row = List.exactlyOne rows

                    let result =
                        {
                            ObservedAt = instant
                            Limit = limit
                            ExpiredAtObservation = DatabaseSession.value 0 row
                            Deleted = DatabaseSession.value 1 row
                        }

                    do! DatabaseSession.commit transaction token
                    return result
                })
