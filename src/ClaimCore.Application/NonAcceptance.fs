namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Bounded retained-control evidence is shared by passive status and every write guard.
module internal NonAcceptance =
    let private validateNode installation (key: FenceKey) (control: RetainedControl) projectedAt =
        let entry = control.Entry

        if
            entry.ActorId <> key.ActorId
            || entry.OperationId <> key.OperationId
            || entry.Subject <> EntryTarget.NonAcceptance key.OperationId
            || entry.RecordedAt <> projectedAt
        then
            Error CoreFault.StoreIntegrityError
        else
            LedgerEntries.validate installation entry
            |> Result.bind (fun _ ->
                let metadata =
                    {
                        LedgerMetadata.InstallationId = installation
                        Sequence = entry.Sequence
                        PreviousHash = EntryHash.toBytes control.PreviousHash
                        OperationId = entry.OperationId
                        ActorId = entry.ActorId
                        RecordedAt = entry.RecordedAt
                        Subject = RecordedTarget.NonAcceptance key.OperationId
                        CreationReference = entry.CreationReference
                        PayloadCommitment = entry.PayloadCommitment
                    }

                if LedgerEntry.hash metadata = entry.EntryHash then
                    Ok true
                else
                    Error CoreFault.StoreIntegrityError)

    let validate installation (key: FenceKey) (evidence: NonAcceptanceEvidence) =
        match evidence.Control, evidence.ProjectedAt with
        | None, None -> Ok false
        | Some control, Some projectedAt -> validateNode installation key control projectedAt
        | _ -> Error CoreFault.StoreIntegrityError

    let read installation (tx: IReadTx) key (accepted: StoredEntry option) =
        task {
            let! evidence = tx.NonAcceptance key

            return
                validate installation key evidence
                |> Result.bind (fun fenced ->
                    match accepted with
                    | Some entry when entry.OperationId <> key.OperationId ->
                        Error CoreFault.StoreIntegrityError
                    | Some entry when fenced && entry.ActorId = key.ActorId ->
                        Error CoreFault.StoreIntegrityError
                    | _ -> Ok fenced)
        }
