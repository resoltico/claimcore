namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql

[<RequireQualifiedAccess>]
type SessionEndReason =
    | Logout
    | Expired
    | Invalid

/// Committed operational changes are returned to the host that owns session telemetry.
type SessionChanges<'value> =
    {
        Value: 'value
        Ended: SessionEndReason list
    }

module SessionChanges =
    let unchanged value = { Value = value; Ended = [] }

/// Operational authentication storage, independent of ledger ordering and authority.
type ISessionStore =
    abstract Create:
        byte array * DateTimeOffset option * TimeSpan * TimeSpan * CancellationToken ->
            Task<SessionChanges<string>>

    abstract Retrieve: string * CancellationToken -> Task<SessionChanges<byte array option>>
    abstract Renew: string * byte array * CancellationToken -> Task<SessionChanges<unit>>
    abstract Remove: string * SessionEndReason * CancellationToken -> Task<SessionChanges<unit>>

module internal SessionRows =
    let maximumPayload = 1048576
    // Every operational expiry query uses the same database-instant deadline predicate.
    let expiredPredicate =
        "(absolute_expiry <= $1 OR last_activity + idle_seconds * interval '1 second' <= $1)"

    let admitPayload (payload: byte array) =
        if payload.Length = 0 || payload.Length > maximumPayload then
            invalidArg (nameof payload) "The protected session ticket must be bounded."

    let now connection token =
        task {
            let! rows = DatabaseSession.rows connection "SELECT clock_timestamp()" [||] token
            return DatabaseSession.instant 0 rows.Head
        }

    let remove connection key token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.sessions WHERE session_key=$1"
            [| box key |]
            token

    let lockRow connection key token =
        DatabaseSession.rows
            connection
            "SELECT protected_ticket,payload_version,absolute_expiry,last_activity,idle_seconds FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
            [| box key |]
            token

    let private wellFormed row =
        let idle = DatabaseSession.value<double> 4 row
        let payload = DatabaseSession.value<byte array> 0 row

        DatabaseSession.value<int> 1 row = 1
        && payload.Length > 0
        && payload.Length <= maximumPayload
        && Double.IsFinite idle
        && idle > 0.

    let private endReason instant row =
        if not (wellFormed row) || instant < DatabaseSession.instant 3 row then
            SessionEndReason.Invalid
        else
            SessionEndReason.Expired

    let valid instant row =
        let activity = DatabaseSession.instant 3 row
        let expiry = DatabaseSession.instant 2 row
        let idle = DatabaseSession.value<double> 4 row

        wellFormed row
        && instant >= activity
        && instant < expiry
        && (instant - activity).TotalSeconds < idle

    let withRow source key token action =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! rows = lockRow connection key token
                    // This must remain a separate statement after FOR UPDATE has acquired the row.
                    let! instant = now connection token

                    let! result =
                        match rows with
                        | [ row ] when valid instant row ->
                            task {
                                let! value = action connection instant row
                                return SessionChanges.unchanged value
                            }
                        | [ row ] ->
                            task {
                                let! removed = remove connection key token

                                return
                                    {
                                        Value = None
                                        Ended = List.replicate removed (endReason instant row)
                                    }
                            }
                        | [] -> Task.FromResult(SessionChanges.unchanged None)
                        | _ -> invalidOp "A session key must identify at most one row."

                    do! transaction.CommitAsync token
                    return result
                })

    let cleanup connection instant token =
        DatabaseSession.execute
            connection
            ("DELETE FROM claimcore.sessions WHERE session_key IN (SELECT session_key FROM claimcore.sessions WHERE "
             + expiredPredicate
             + " ORDER BY absolute_expiry LIMIT 128 FOR UPDATE SKIP LOCKED)")
            [| box instant |]
            token

[<Sealed>]
type internal SessionStore(source: NpgsqlDataSource) =
    interface ISessionStore with
        member _.Create(payload, expiry, idle, absolute, token) =
            SessionRows.admitPayload payload

            if idle <= TimeSpan.Zero || absolute < idle then
                invalidArg (nameof idle) "Session lifetimes must be positive and ordered."

            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _ = Transactions.configure connection Transactions.timeouts token

                        let! _ =
                            DatabaseSession.execute
                                connection
                                "SELECT pg_advisory_xact_lock(621783092451::bigint)"
                                [||]
                                token

                        let! instant = SessionRows.now connection token

                        let deadline =
                            (min (instant + absolute) (defaultArg expiry DateTimeOffset.MaxValue))
                                .ToUniversalTime()

                        if deadline <= instant then
                            invalidOp "The session ticket has expired."

                        let! expired = SessionRows.cleanup connection instant token

                        let! counts =
                            DatabaseSession.rows
                                connection
                                "SELECT count(*) FROM claimcore.sessions WHERE absolute_expiry > $1 AND last_activity + idle_seconds * interval '1 second' > $1"
                                [| box instant |]
                                token

                        if DatabaseSession.value<int64> 0 counts.Head >= 10000L then
                            invalidOp "The OIDC session store is full."

                        let key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 32)

                        let! _ =
                            DatabaseSession.execute
                                connection
                                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds) VALUES($1,$2,1,$3,$4,$3,$5)"
                                [|
                                    box key
                                    box payload
                                    box instant
                                    box deadline
                                    box idle.TotalSeconds
                                |]
                                token

                        do! transaction.CommitAsync token

                        return
                            {
                                Value = key
                                Ended = List.replicate expired SessionEndReason.Expired
                            }
                    })

        member _.Retrieve(key, token) =
            SessionRows.withRow source key token (fun connection instant row ->
                task {
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "UPDATE claimcore.sessions SET last_activity=$2 WHERE session_key=$1"
                            [| box key; box instant |]
                            token

                    return Some(DatabaseSession.value<byte array> 0 row)
                })

        member _.Renew(key, payload, token) =
            task {
                SessionRows.admitPayload payload

                let! changes =
                    SessionRows.withRow source key token (fun connection _ _ ->
                        task {
                            let! _ =
                                DatabaseSession.execute
                                    connection
                                    "UPDATE claimcore.sessions SET protected_ticket=$2 WHERE session_key=$1"
                                    [| box key; box payload |]
                                    token

                            return Some()
                        })

                return { Value = (); Ended = changes.Ended }
            }

        member _.Remove(key, reason, token) =
            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _ = Transactions.configure connection Transactions.timeouts token
                        let! removed = SessionRows.remove connection key token
                        do! transaction.CommitAsync token

                        return
                            {
                                Value = ()
                                Ended = List.replicate removed reason
                            }
                    })
