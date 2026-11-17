namespace ClaimCore.Application.Storage

open ClaimCore.Domain

/// Authentication hints and identity creation serialize on the same exact principal key.
module internal PrincipalLock =
    let acquire connection principal token =
        DatabaseSession.execute
            connection
            "SELECT pg_advisory_xact_lock(hashtextextended($1,173701))"
            [| box (PrincipalKey.naturalKey principal) |]
            token
