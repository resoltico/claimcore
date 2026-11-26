namespace ClaimCore.Application.Storage

open ClaimCore.Domain

/// Authentication hints and identity creation serialize on the same exact principal key.
module internal PrincipalLock =
    let private exactHash key = $"hashtextextended({key},173701)"

    let acquireStagedActors connection token =
        let hashed = exactHash "principal_key"

        DatabaseSession.execute
            connection
            ($"""DO $$ BEGIN
                   PERFORM pg_advisory_xact_lock(key)
                   FROM (SELECT DISTINCT {hashed} AS key FROM pg_temp.actors ORDER BY key) AS actors;
                 END $$""")
            [||]
            token

    let acquire connection principal token =
        DatabaseSession.execute
            connection
            ("SELECT pg_advisory_xact_lock(" + exactHash "$1" + ")")
            [| box (PrincipalKey.naturalKey principal) |]
            token
