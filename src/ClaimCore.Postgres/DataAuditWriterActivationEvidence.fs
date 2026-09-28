namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open DataAuditCommon
open WitnessProtocolReconciliation

/// Historical signature, canonical candidate, and independent witness evidence for W2.
module internal DataAuditWriterActivationEvidence =
    let private signed (row: WriterActivationAuditRow) =
        let value = row.Evidence

        row.SignerPurpose = "CHECKPOINT"
        && row.SignerPublicKey.Length = 32
        && row.SignerHolder = value.CheckpointHolderActorId
        && row.SignerRegisteredSequence < value.W1Sequence
        && (row.SignerRetiredSequence
            |> Option.forall (fun sequence -> row.SettlementSequence < sequence))
        && SHA256.HashData(value.SignedReport) = value.ReportSha256
        && SHA256.HashData(value.SignedFence) = value.FenceSha256
        && SHA256.HashData(value.SignedSupplement) = value.SupplementSha256
        && value.ReportSignature.Length = 64
        && ManagedCopySignature.verify row.SignerPublicKey value.SignedFence value.FenceSignature
        && ManagedCopySignature.verify
            row.SignerPublicKey
            value.SignedSupplement
            value.SupplementSignature

    let verify (witness: WitnessProtocol) cutoff (row: WriterActivationAuditRow) =
        let value = row.Evidence
        let canonical = WriterActivationCandidate.encode value

        try
            if
                not (signed row)
                || value.WriterGeneration <> row.HandoffGeneration
                || value.W1Sequence <> row.HandoffSequence
                || value.W1Hash <> row.HandoffHash
                || row.ActivationId <> WriterActivationCandidate.activationId value.HandoffId
                || row.Canonical <> canonical
                || row.CandidateSha256 <> SHA256.HashData(canonical)
                || row.IntentSequence <> value.W1Sequence + 1L
                || row.SettlementSequence <> row.IntentSequence + 1L
                || row.SettlementSequence > cutoff
                || row.SettlementEpoch <> witness.Identity.Epoch
            then
                corrupt ()

            witnessProof (fun () ->
                witness.VerifyHistoricalTip(value.W1Sequence, value.W1Hash)
                witness.VerifyHistoricalTip(row.IntentSequence, row.IntentHash)
                witness.VerifyHistoricalTip(row.SettlementSequence, row.SettlementHash)

                WriterActivationWitness.verifyHistorical
                    witness
                    row.ActivationId
                    canonical
                    (row.IntentSequence, row.IntentHash)
                    (row.SettlementSequence, row.SettlementHash)
                |> ignore)
        finally
            CryptographicOperations.ZeroMemory(canonical)
