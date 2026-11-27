namespace ClaimCore.Application.Storage

open ClaimCore.Domain

/// Authentication hints and identity creation serialize on the same exact principal key.
module internal PrincipalLock =
    let private exactHash key = $"hashtextextended({key},173701)"

    // The ledger head already serializes competing person transitions. The order
    // also covers the old key on a rebind while a callback may be promoting it.
    let acquireChange connection (keys: string array) token =
        let hashed = exactHash "principal_key"

        DatabaseSession.execute
            connection
            ($"""SELECT pg_advisory_xact_lock(key)
                  FROM (SELECT DISTINCT {hashed} AS key FROM unnest($1::text[]) AS keys(principal_key) ORDER BY key) AS identities""")
            [| box keys |]
            token

    let acquireStagedActors connection token =
        let hashed = exactHash "principal_key"

        DatabaseSession.execute
            connection
            ($"""DO $$ BEGIN
                   PERFORM pg_advisory_xact_lock(key)
                   FROM (SELECT DISTINCT {hashed} AS key FROM (
                       SELECT principal_key FROM pg_temp.actors UNION SELECT principal_key FROM claimcore.actors
                   ) AS bindings ORDER BY key) AS actors;
                 END $$""")
            [||]
            token

    let acquire connection principal token =
        DatabaseSession.execute
            connection
            ("SELECT pg_advisory_xact_lock(" + exactHash "$1" + ")")
            [| box (PrincipalKey.naturalKey principal) |]
            token
