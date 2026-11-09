namespace ClaimCore.Postgres

open System

/// Access stays inside the caller's snapshot or head-locked transaction; no process clock/cache.
module internal ExpiryFrontierStorage =
    let private instant (ticks: int64) = DateTimeOffset(ticks, TimeSpan.Zero)

    let read connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT expired_before_ticks FROM claimcore.ledger_head WHERE singleton"
                    [||]
                    token

            match rows with
            | [ row ] -> return DatabaseSession.optional<int64> 0 row |> Option.map instant
            | _ -> return raise (RecordCorruptException "Expiry frontier is absent.")
        }

    let advance connection (candidate: DateTimeOffset) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT claimcore.advance_expiry_frontier($1)"
                    [| box candidate.UtcTicks |]
                    token

            match rows with
            | [ row ] -> return DatabaseSession.value<int64> 0 row |> instant
            | _ -> return raise (RecordCorruptException "Expiry frontier update is absent.")
        }
