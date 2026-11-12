namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

module internal CheckpointEvidence =
    let installation actual (checkpoint: InstallationCheckpoint) =
        if checkpoint.InstallationId = actual then
            Ok()
        else
            Error
                {
                    Sequence = Some checkpoint.Position.Sequence
                    Finding = VerificationFinding.CheckpointInstallation
                }

    let frontier connection (checkpoint: InstallationCheckpoint option) token =
        task {
            let! actual = ExpiryFrontierStorage.read connection token

            let valid =
                match checkpoint |> Option.bind _.ExpiredBefore, actual with
                | None, _ -> true
                | Some expected, Some actual -> actual >= expected
                | Some _, None -> false

            return
                if valid then
                    Ok()
                else
                    Error
                        {
                            Sequence = None
                            Finding = VerificationFinding.ExpiryFrontier
                        }
        }

    let read connection installation token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,entry_hash,expired_before_ticks FROM claimcore.ledger_head WHERE singleton"
                    [||]
                    token

            match rows with
            | [ row ] ->
                let position =
                    StoredEntries.position
                        (DatabaseSession.value<int64> 0 row)
                        (DatabaseSession.value<byte array> 1 row)

                let frontier =
                    DatabaseSession.optional<int64> 2 row
                    |> Option.map (fun ticks -> DateTimeOffset(ticks, TimeSpan.Zero))

                return
                    InstallationCheckpoint.create installation position frontier
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Invalid installation checkpoint."))
            | _ -> return raise (RecordCorruptException "Installation checkpoint is absent.")
        }
