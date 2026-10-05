namespace ClaimCore.Database

open System
open System.Threading
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// The owner transaction already holds actor and case authority. These SHARE locks close
/// the copy set while its signed registry and deletion receipts are checked.
module internal DatabaseTerminalCopyAbsenceAdmission =
    let lockCopies connection transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "LOCK TABLE claimcore.managed_copies,claimcore.managed_copy_events,"
                    + "claimcore.managed_copy_deletion_approvals,"
                    + "claimcore.managed_copy_deletion_approval_uses,"
                    + "claimcore.managed_copy_external_publications,"
                    + "claimcore.managed_copy_signers IN SHARE MODE",
                    connection,
                    transaction
                )

            let! _ = command.ExecuteNonQueryAsync(ct)
            return ()
        }

    let verifyCase
        connection
        transaction
        (witness: WitnessProtocol)
        caseId
        pruneEventId
        cutoffSequence
        cutoffHash
        writerGeneration
        ct
        =
        task {
            let! stored = CaseTombstoneTerminalRead.lock connection transaction caseId
            let! holds = CaseTombstoneRead.activeHolds connection transaction caseId

            let! unadopted =
                DataAuditExternalPublications.hasUnadoptedForCase connection transaction caseId

            let! tip = witness.Snapshot(ct)

            return
                match stored with
                | Some value ->
                    value.Phase = "ERASURE_PENDING"
                    && value.PruneEventId = pruneEventId
                    && value.CutoffSequence = cutoffSequence
                    && value.CutoffHash = cutoffHash
                    && value.CopyAbsenceEventId.IsNone
                    && value.WriterGeneration = writerGeneration
                    && tip.WriterGeneration = writerGeneration
                    && tip.TipSequence > cutoffSequence
                    && not tip.HandoffPending
                    && holds.IsEmpty
                    && not unadopted
                | None -> false
        }
