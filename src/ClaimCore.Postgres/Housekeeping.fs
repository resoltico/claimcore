namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

type HousekeepingReport =
    {
        ObservedAt: DateTimeOffset
        SessionsDeleted: int64
        AccessRequestsDeleted: int64
    }

/// Operational expiry is independent of ledger order. Each locked candidate is rechecked before deletion.
module Housekeeping =
    let DefaultBatch = 128
    let MaximumBatch = 1000

    let internal prune (source: NpgsqlDataSource) limit (token: CancellationToken) =
        if limit < 1 || limit > MaximumBatch then
            invalidArg (nameof limit) "Use a bounded positive housekeeping batch."

        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Baseline.require connection token
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! instant = SessionRows.now connection token

                    let sessionsSql =
                        "WITH candidates AS MATERIALIZED (SELECT session_key FROM claimcore.sessions WHERE "
                        + SessionRows.expiredPredicate
                        + " ORDER BY absolute_expiry,session_key LIMIT $2 FOR UPDATE SKIP LOCKED), deleted AS (DELETE FROM claimcore.sessions s USING candidates c WHERE s.session_key=c.session_key AND "
                        + SessionRows.expiredPredicate
                        + " RETURNING 1) SELECT count(*) FROM deleted"

                    let! sessions =
                        DatabaseSession.rows
                            connection
                            sessionsSql
                            [| box instant; box limit |]
                            token

                    let! requests =
                        DatabaseSession.rows
                            connection
                            ("WITH candidates AS MATERIALIZED (SELECT issuer,principal_kind,subject FROM claimcore.access_request WHERE "
                             + AccessRequests.expiredPredicate ""
                             + " ORDER BY last_seen,issuer,principal_kind,subject LIMIT $2 FOR UPDATE SKIP LOCKED), deleted AS (DELETE FROM claimcore.access_request a USING candidates c WHERE (a.issuer,a.principal_kind,a.subject)=(c.issuer,c.principal_kind,c.subject) AND "
                             + AccessRequests.expiredPredicate "a."
                             + " RETURNING 1) SELECT count(*) FROM deleted")
                            [| box instant; box limit |]
                            token

                    do! DatabaseSession.commit transaction token

                    return
                        {
                            ObservedAt = instant
                            SessionsDeleted = DatabaseSession.value<int64> 0 sessions.Head
                            AccessRequestsDeleted = DatabaseSession.value<int64> 0 requests.Head
                        }
                })
