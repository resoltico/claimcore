namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Exact comparison of primary target, signed checkpoint key and witness handoff chain.
module internal DataAuditWriterHandoffEvidence =
    let private same (row: WriterHandoffAuditRow) (witnessRow: WriterHandoffEvidence) =
        row.HandoffId = witnessRow.HandoffId
        && row.OldGeneration = witnessRow.OldGeneration
        && row.NewGeneration = witnessRow.NewGeneration
        && row.CheckpointSigningKeyId = witnessRow.CheckpointSigningKeyId
        && row.ApprovalOneId = witnessRow.ApprovalOneId
        && row.ApprovalTwoId = witnessRow.ApprovalTwoId
        && row.PrepareCanonical = witnessRow.PrepareCanonical
        && row.PrepareSignature = witnessRow.PrepareSignature
        && row.PrepareCandidate = witnessRow.PrepareCandidateSha256
        && row.PrepareSequence = witnessRow.PrepareSequence
        && row.PrepareHash = witnessRow.PrepareHash
        && Some row.SettlementCanonical = witnessRow.SettlementCanonical
        && Some row.SettlementSignature = witnessRow.SettlementSignature
        && Some row.SettlementCandidate = witnessRow.SettlementCandidateSha256
        && Some row.SettlementSequence = witnessRow.SettlementSequence
        && Some row.SettlementHash = witnessRow.SettlementHash

    let private signed (row: WriterHandoffAuditRow) =
        row.SignerPurpose = "CHECKPOINT"
        && row.SignerPublicKey.Length = 32
        && row.SignerRegisteredSequence < row.PrepareSequence
        && (row.SignerRetiredSequence
            |> Option.forall (fun value -> row.SettlementSequence < value))
        && SHA256.HashData(row.PrepareCanonical) = row.PrepareCandidate
        && SHA256.HashData(row.SettlementCanonical) = row.SettlementCandidate
        && ManagedCopySignature.verify row.SignerPublicKey row.PrepareCanonical row.PrepareSignature
        && ManagedCopySignature.verify
            row.SignerPublicKey
            row.SettlementCanonical
            row.SettlementSignature

    let private plaintext
        (witness: WitnessProtocol)
        operationId
        phase
        (evidence: Evidence)
        (expected: byte array)
        =
        let aad = witness.AssociatedData(operationId, phase)

        let decoded =
            witness.KeyCustody.Decrypt(evidence.Ticket.KeyId, aad, evidence.EncryptedPayload)

        try
            if decoded <> expected then
                corrupt ()
        finally
            CryptographicOperations.ZeroMemory(decoded)

    let private verifyCheckpoints
        (witness: WitnessProtocol)
        (witnessRow: WriterHandoffEvidence)
        (row: WriterHandoffAuditRow)
        ct
        =
        witnessProofAsync (fun () ->
            task {
                for sequence, hash in
                    [
                        witnessRow.PreviousSequence, witnessRow.PreviousHash
                        row.PrepareSequence, row.PrepareHash
                        row.SettlementSequence, row.SettlementHash
                    ] do
                    do! witness.VerifyHistoricalTip(sequence, hash, ct)
            })

    let verify
        (witness: WitnessProtocol)
        cutoff
        (row: WriterHandoffAuditRow)
        (ct: CancellationToken)
        =
        task {
            let! retained = witness.EvidenceStore.TryReadHandoff(row.HandoffId, ct)
            let witnessRow = retained |> Option.defaultWith corrupt

            if
                not (same row witnessRow)
                || not (signed row)
                || row.OldGeneration < 1L
                || row.NewGeneration <> row.OldGeneration + 1L
                || row.PrepareSequence <> witnessRow.PreviousSequence + 1L
                || row.SettlementSequence <> row.PrepareSequence + 1L
                || row.SettlementSequence > cutoff
                || row.ApprovalOneId = row.ApprovalTwoId
            then
                corrupt ()

            do! verifyCheckpoints witness witnessRow row ct

            let! retainedIntent =
                witness.EvidenceStore.TryReadEvidence(row.HandoffId, Intent, ct)

            let intent = retainedIntent |> Option.defaultWith corrupt

            let! retainedSettlement =
                witness.EvidenceStore.TryReadEvidence(row.HandoffId, SettledAuthority, ct)

            let settled = retainedSettlement |> Option.defaultWith corrupt

            if
                intent.Ticket.Sequence <> row.PrepareSequence
                || intent.Ticket.EntryHash <> row.PrepareHash
                || intent.Ticket.ScopeKind <> Installation
                || settled.Ticket.Sequence <> row.SettlementSequence
                || settled.Ticket.EntryHash <> row.SettlementHash
                || settled.Ticket.ScopeKind <> Installation
                || settled.Ticket.KeyId <> intent.Ticket.KeyId
            then
                corrupt ()

            plaintext witness row.HandoffId "INTENT" intent row.PrepareCanonical

            let settlement =
                WriterHandoffEvidenceHash.settlement row.PrepareCanonical row.SettlementCanonical

            try
                plaintext witness row.HandoffId "SETTLED_AUTHORITY" settled settlement
            finally
                CryptographicOperations.ZeroMemory(settlement)

            return witnessRow.PrepareRecordedAt, witnessRow.NewCapabilitySha256
        }
