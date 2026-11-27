namespace ClaimCore.Application.Storage




open ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Domain

module internal SessionRows =
    // Start locks browser order. Completion takes order, then principal, then ticket rows.
    // Person transitions take head then principals. Termination and restore take head
    // then order. Retrieval and renewal only lock the ticket and read committed facts.
    let lockOrder connection token =
        DatabaseSession.rows
            connection
            "SELECT position FROM claimcore.browser_flow_order FOR UPDATE"
            [||]
            token

    let advanceOrder connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "UPDATE claimcore.browser_flow_order SET position=position+1 RETURNING position"
                    [||]
                    token

            return DatabaseSession.value<int64> 0 (List.exactlyOne rows)
        }

    let flowLifetime = TimeSpan.FromMinutes 15.

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

    let terminationCutoff connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    """SELECT greatest(o.termination_floor,r.credential_floor)
                       FROM claimcore.browser_flow_order o CROSS JOIN claimcore.recovery_generation r"""
                    [||]
                    token

            return
                DatabaseSession.optional<DateTime> 0 (List.exactlyOne rows)
                |> Option.map DateTimeOffset
        }

    let remove connection key token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.sessions WHERE session_key=$1"
            [| box key |]
            token

    let pruneFlows connection (instant: DateTimeOffset) token =
        task {
            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.browser_flows WHERE expires_at <= $1"
                    [| box instant |]
                    token

            // Callers hold the order row, so an earlier start cannot be enqueued after this prune.
            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.browser_session_cuts WHERE expires_at <= $1"
                    [| box instant |]
                    token

            return ()
        }

    let pruneResults connection instant token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    """UPDATE claimcore.browser_flow_order SET termination_floor=greatest(termination_floor,$1)
                       RETURNING termination_floor"""
                    [| box (OperationLifetime.cutoff instant) |]
                    token

            let floor = DatabaseSession.instant 0 (List.exactlyOne rows)

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.session_termination_results WHERE requested_at < $1"
                    [| box floor |]
                    token

            return ()
        }

    let terminate connection position (instant: DateTimeOffset) (keys: string array) token =
        task {
            do! pruneFlows connection instant token

            let! _ =
                DatabaseSession.execute
                    connection
                    """INSERT INTO claimcore.browser_session_cuts(principal_key,position,expires_at)
                       SELECT key,$1,greatest($2,(SELECT max(expires_at) FROM claimcore.browser_flows))
                       FROM unnest($3::text[]) key
                       ON CONFLICT (principal_key) DO UPDATE SET position=EXCLUDED.position,
                           expires_at=greatest(browser_session_cuts.expires_at,EXCLUDED.expires_at)"""
                    [| box position; box (instant + flowLifetime); box keys |]
                    token

            return!
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.sessions WHERE principal_key=ANY($1::text[])"
                    [| box keys |]
                    token
        }

    let lockRow connection key token =
        DatabaseSession.rows
            connection
            "SELECT protected_ticket,payload_version,absolute_expiry,last_activity,idle_seconds,principal_key,recovery_generation,credential_actor,credential_generation FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
            [| box key |]
            token

    let private wellFormed row =
        let idle = DatabaseSession.value<double> 4 row
        let payload = DatabaseSession.value<byte array> 0 row

        DatabaseSession.value<int> 1 row = 2
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

    let current connection row token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    """SELECT EXISTS(SELECT FROM claimcore.recovery_generation WHERE generation=$1)
                              AND ($2::uuid IS NULL OR EXISTS(SELECT FROM claimcore.actors WHERE
                                  actor_id=$2 AND principal_key=$3 AND credential_generation=$4 AND enabled))"""
                    [|
                        box (DatabaseSession.value<Guid> 6 row)
                        DatabaseSession.parameter (DatabaseSession.optional<Guid> 7 row)
                        box (DatabaseSession.value<string> 5 row)
                        DatabaseSession.parameter (DatabaseSession.optional<int64> 8 row)
                    |]
                    token

            return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
        }

    let withRow source installation key token action =
        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    do! Baseline.requireExpected installation connection token
                    FailureDiagnostics.phase FailurePhase.Session
                    let! rows = lockRow connection key token
                    // This must remain a separate statement after FOR UPDATE has acquired the row.
                    let! instant = now connection token

                    let! credentialCurrent =
                        match rows with
                        | [ row ] when valid instant row -> current connection row token
                        | _ -> Task.FromResult false

                    let! result =
                        match rows with
                        | [ row ] when credentialCurrent ->
                            task { return! action connection instant row }
                        | [ row ] ->
                            task {
                                let! removed = remove connection key token

                                return
                                    {
                                        Value = None
                                        Ended =
                                            List.replicate
                                                removed
                                                (if valid instant row then
                                                     SessionEndReason.Invalid
                                                 else
                                                     endReason instant row)
                                    }
                            }
                        | [] -> Task.FromResult(SessionChanges.unchanged None)
                        | _ -> invalidOp "A session key must identify at most one row."

                    token.ThrowIfCancellationRequested()
                    FailureDiagnostics.phase FailurePhase.Commit
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
type internal SessionStore(source: NpgsqlDataSource, installation: Installation) =
    interface ISessionStore with
        member _.StartFlow token =
            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _ = Transactions.configure connection Transactions.timeouts token
                        do! Baseline.requireExpected installation connection token
                        let! position = SessionRows.advanceOrder connection token
                        let! instant = SessionRows.now connection token

                        do! SessionRows.pruneFlows connection instant token

                        let! counts =
                            DatabaseSession.rows
                                connection
                                "SELECT count(*) FROM claimcore.browser_flows"
                                [||]
                                token

                        if DatabaseSession.value<int64> 0 (List.exactlyOne counts) >= 10000L then
                            raise (IO.IOException "The browser sign-in flow store is full.")

                        let id = Guid.NewGuid()

                        let! _ =
                            DatabaseSession.execute
                                connection
                                "INSERT INTO claimcore.browser_flows(flow_id,position,expires_at) VALUES ($1,$2,$3)"
                                [|
                                    box id
                                    box position
                                    box (instant + SessionRows.flowLifetime)
                                |]
                                token

                        do! transaction.CommitAsync token
                        return id
                    })

        member _.Create(principal, login, protect, expiry, idle, absolute, token) =
            if idle <= TimeSpan.Zero || absolute < idle then
                invalidArg (nameof idle) "Session lifetimes must be positive and ordered."

            DatabaseSession.transaction
                source
                Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _ = Transactions.configure connection Transactions.timeouts token
                        do! Baseline.requireExpected installation connection token
                        FailureDiagnostics.phase FailurePhase.Session
                        // The order lock also serializes the active-ticket capacity check.
                        let! _ = SessionRows.lockOrder connection token
                        let! _ = PrincipalLock.acquire connection principal token

                        let! instant = SessionRows.now connection token

                        let! state = Credentials.current connection principal token

                        let! proof =
                            task {
                                match login with
                                | None -> return None
                                | Some(flow, proof) ->
                                    let! rows =
                                        DatabaseSession.rows
                                            connection
                                            """DELETE FROM claimcore.browser_flows f
                                                       WHERE flow_id=$1 AND expires_at > $2
                                                       AND f.position > coalesce(
                                                           (SELECT position FROM claimcore.browser_session_cuts
                                                            WHERE principal_key=$3),0)
                                                       RETURNING position"""
                                            [|
                                                box flow
                                                box instant
                                                box (PrincipalKey.naturalKey principal)
                                            |]
                                            token

                                    if rows.IsEmpty then
                                        raise (
                                            Security.Authentication.AuthenticationException
                                                "The browser sign-in flow is unavailable."
                                        )

                                    return Some proof
                            }

                        let credential =
                            Credentials.browser installation.InstallationId instant state proof

                        let payload = protect credential
                        SessionRows.admitPayload payload

                        let actor, generation =
                            match credential with
                            | BrowserCredential.StatusOnly -> None, None
                            | BrowserCredential.Protected(_, _, actor, generation) ->
                                Some(ActorId.value actor), Some(Sequence.value generation)

                        let deadline =
                            (min (instant + absolute) (defaultArg expiry DateTimeOffset.MaxValue))
                                .ToUniversalTime()

                        if deadline <= instant then
                            raise (
                                Security.Authentication.AuthenticationException
                                    "The session ticket has expired."
                            )

                        let! expired = SessionRows.cleanup connection instant token

                        let! counts =
                            DatabaseSession.rows
                                connection
                                "SELECT count(*) FROM claimcore.sessions WHERE absolute_expiry > $1 AND last_activity + idle_seconds * interval '1 second' > $1"
                                [| box instant |]
                                token

                        if DatabaseSession.value<int64> 0 counts.Head >= 10000L then
                            raise (IO.IOException "The OIDC session store is full.")

                        let key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 32)

                        let! _ =
                            DatabaseSession.execute
                                connection
                                "INSERT INTO claimcore.sessions(session_key,protected_ticket,payload_version,created_at,absolute_expiry,last_activity,idle_seconds,principal_key,recovery_generation,credential_actor,credential_generation) VALUES($1,$2,2,$3,$4,$3,$5,$6,$7,$8,$9)"
                                [|
                                    box key
                                    box payload
                                    box instant
                                    box deadline
                                    box idle.TotalSeconds
                                    box (PrincipalKey.naturalKey principal)
                                    box state.RecoveryGeneration
                                    DatabaseSession.parameter actor
                                    DatabaseSession.parameter generation
                                |]
                                token

                        token.ThrowIfCancellationRequested()
                        FailureDiagnostics.phase FailurePhase.Commit
                        do! transaction.CommitAsync token

                        return
                            {
                                Value = key
                                Ended = List.replicate expired SessionEndReason.Expired
                            }
                    })

        member _.Retrieve(key, token) =
            SessionRows.withRow source installation key token (fun connection instant row ->
                task {
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "UPDATE claimcore.sessions SET last_activity=$2 WHERE session_key=$1"
                            [| box key; box instant |]
                            token

                    return
                        SessionChanges.unchanged (
                            Some
                                {
                                    PrincipalKey = DatabaseSession.value<string> 5 row
                                    Payload = DatabaseSession.value<byte array> 0 row
                                }
                        )
                })

        member _.Renew(key, principal, payload, validateExisting, token) =
            task {
                SessionRows.admitPayload payload

                let! changes =
                    SessionRows.withRow source installation key token (fun connection _ row ->
                        task {
                            if
                                DatabaseSession.value<string> 5 row
                                <> PrincipalKey.naturalKey principal
                                || not (validateExisting (DatabaseSession.value<byte array> 0 row))
                            then
                                let! removed = SessionRows.remove connection key token

                                return
                                    {
                                        Value = None
                                        Ended = List.replicate removed SessionEndReason.Invalid
                                    }
                            else
                                let! _ =
                                    DatabaseSession.execute
                                        connection
                                        "UPDATE claimcore.sessions SET protected_ticket=$2 WHERE session_key=$1"
                                        [| box key; box payload |]
                                        token

                                return SessionChanges.unchanged (Some())
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
                        do! Baseline.requireExpected installation connection token
                        FailureDiagnostics.phase FailurePhase.Session
                        let! removed = SessionRows.remove connection key token
                        token.ThrowIfCancellationRequested()
                        FailureDiagnostics.phase FailurePhase.Commit
                        do! transaction.CommitAsync token

                        return
                            {
                                Value = ()
                                Ended = List.replicate removed reason
                            }
                    })
