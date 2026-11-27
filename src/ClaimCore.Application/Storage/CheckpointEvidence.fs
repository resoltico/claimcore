namespace ClaimCore.Application.Storage

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

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
                    Attachment = None
                }

    let trustedInstallation actual (checkpoint: InstallationCheckpoint) =
        installation actual checkpoint
        |> Result.bind (fun () ->
            if checkpoint.Position.Sequence < Sequence.first then
                Error
                    {
                        Sequence = Some checkpoint.Position.Sequence
                        Finding = VerificationFinding.CheckpointPredatesBootstrap
                        Attachment = None
                    }
            else
                Ok())

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
                            Attachment = None
                        }
        }

    let fromStorage installation sequence hash (ticks: int64 option) =
        let position = StoredEntries.position sequence hash

        let frontier =
            ticks |> Option.map (fun ticks -> DateTimeOffset(ticks, TimeSpan.Zero))

        InstallationCheckpoint.create installation position frontier
        |> Option.defaultWith (fun () ->
            raise (RecordCorruptException "Invalid installation checkpoint."))

    let retainsPosition connection (position: LedgerPosition) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM claimcore.ledger WHERE sequence=$1 AND entry_hash=$2)"
                    [|
                        box (Sequence.value position.Sequence)
                        box (EntryHash.toBytes position.EntryHash)
                    |]
                    token

            return DatabaseSession.value<bool> 0 (List.exactlyOne rows)
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
                return
                    fromStorage
                        installation
                        (DatabaseSession.value 0 row)
                        (DatabaseSession.value 1 row)
                        (DatabaseSession.optional 2 row)
            | _ -> return raise (RecordCorruptException "Installation checkpoint is absent.")
        }
