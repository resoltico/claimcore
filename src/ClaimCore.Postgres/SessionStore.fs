namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Operational authentication storage, independent of ledger ordering and authority.
type ISessionStore =
    abstract Create:
        byte array * DateTimeOffset option * TimeSpan * TimeSpan * CancellationToken -> Task<string>

    abstract Retrieve: string * CancellationToken -> Task<byte array option>
    abstract Renew: string * byte array * CancellationToken -> Task<unit>
    abstract Remove: string * CancellationToken -> Task<unit>

module internal SessionRows =
    let maximumPayload = 1048576

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

    let valid instant row =
        let activity = DatabaseSession.instant 3 row
        let expiry = DatabaseSession.instant 2 row
        let idle = DatabaseSession.value<double> 4 row
        let payload = DatabaseSession.value<byte array> 0 row

        DatabaseSession.value<int> 1 row = 1
        && payload.Length > 0
        && payload.Length <= maximumPayload
        && Double.IsFinite idle
        && idle > 0.
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
                        | [ row ] when valid instant row -> action connection instant row
                        | _ -> task { let! _ = remove connection key token in return None }

                    do! transaction.CommitAsync token
                    return result
                })

    let cleanup connection instant token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.sessions WHERE session_key IN (SELECT session_key FROM claimcore.sessions WHERE absolute_expiry <= $1 OR last_activity + idle_seconds * interval '1 second' <= $1 ORDER BY absolute_expiry LIMIT 128 FOR UPDATE SKIP LOCKED)"
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

                        let! _ = SessionRows.cleanup connection instant token

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
                        return key
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

                let! _ =
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

                return ()
            }

        member _.Remove(key, token) =
            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _ = Transactions.configure connection Transactions.timeouts token
                        let! _ = SessionRows.remove connection key token
                        do! transaction.CommitAsync token
                        return ()
                    })
