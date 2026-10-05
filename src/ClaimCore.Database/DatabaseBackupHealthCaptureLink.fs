namespace ClaimCore.Database

open System.Threading
open System
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// The local SEALED receipt is only a capture consistency prerequisite; it is not an
/// independent archive or RETAINED proof and never by itself authorizes health.
module internal DatabaseBackupHealthCaptureLink =
    let verify ownerConnection (policy: BackupHealthPolicy) (source: BackupHealthEvidence) =
        task {
            let auditConnection =
                DatabaseWitnessInputs.witnessAuditConnection ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Backup health witness audit is unavailable.")

            let ownerBuilder = OwnerConnection.builder ownerConnection
            use owner = new NpgsqlConnection(ownerBuilder.ConnectionString)
            owner.Open()
            let identity, _, _ = DatabaseVerifyData.identity owner

            let custody =
                DatabaseWitnessInputs.keyRing ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Backup health witness custody is unavailable.")

            let witness =
                try
                    let store = Store.OpenAudit(auditConnection, identity)

                    try
                        new WitnessProtocol(store, custody, identity)
                    with _ ->
                        (store :> IDisposable).Dispose()
                        reraise ()
                with _ ->
                    custody.Dispose()
                    reraise ()

            use witness = witness
            do! witness.AdmitReadOnly(CancellationToken.None)

            let! receipt =
                DatabaseBackupCaptureReconciliation.inspect
                    owner
                    witness
                    policy.ArchiveRoot
                    policy.CheckpointRoot
                    source.LeaseId

            if
                receipt.CycleReceiptId <> source.CycleId
                || receipt.LeaseId <> source.LeaseId
                || receipt.Nonce <> source.CaptureNonce
                || receipt.WitnessSequence <> source.BackupCaptureSequence
                || receipt.WitnessHash <> source.BackupCaptureHash
                || receipt.ReceiptSha256 <> source.CaptureReceiptSha256
            then
                invalidOp "Backup health source differs from SEALED capture receipt."
        }
