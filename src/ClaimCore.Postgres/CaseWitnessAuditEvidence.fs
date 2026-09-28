namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Full ciphertext proof when present; after a witnessed CASE-only prune, exact immutable
/// ticket/target metadata proof. The whole-case prune audit and global journal scan must also
/// complete in the same full audit before admission: erased plaintext cannot be re-read.
module internal CaseWitnessAuditEvidence =
    let private phaseName =
        function
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | _ -> corrupt ()

    let private full
        (witness: WitnessProtocol)
        settlement
        operation
        sequence
        epoch
        hash
        digest
        caseId
        =
        witnessProof (fun () ->
            match settlement with
            | SettledAccepted ->
                witness.VerifyAcceptedEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId
                )
            | SettledRevoked ->
                witness.VerifyRevokedEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId
                )
            | SettledAuthority ->
                witness.VerifyAuthorityEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId
                )
            | _ -> corrupt ())

    let private metadata (witness: WitnessProtocol) cutoff caseId operation phase =
        let row =
            witness.EvidenceStore.TryReadMetadataOperation(operation, phase)
            |> Option.defaultWith corrupt

        if
            row.Ticket.Sequence > cutoff
            || row.Ticket.Epoch <> witness.Identity.Epoch
            || row.Ticket.OperationId <> operation
            || row.Ticket.Phase <> phase
            || row.Ticket.ScopeKind <> Case
            || row.Ticket.SubjectCaseId <> Some caseId
        then
            corrupt ()

        row

    let private target
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (receipt: StoredWitnessPruneReceipt)
        (row: MetadataRecord)
        =
        use command =
            new NpgsqlCommand(
                "SELECT prune_event_id,operation_id,phase,witness_epoch,entry_hash,"
                + "payload_sha256 FROM claimcore.case_erasure_prune_targets "
                + "WHERE case_id=@case AND sequence=@sequence",
                connection,
                transaction
            )

        Sql.uuid command "case" receipt.CaseId
        Sql.integer command "sequence" row.Ticket.Sequence
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            corrupt ()

        let ticket = row.Ticket

        if
            reader.GetGuid(0) <> receipt.EventId
            || reader.GetGuid(1) <> ticket.OperationId
            || reader.GetString(2)
               <> (if ticket.Phase = Intent then
                       "INTENT"
                   else
                       phaseName ticket.Phase)
            || reader.GetInt64(3) <> ticket.Epoch
            || reader.GetFieldValue<byte array>(4) <> ticket.EntryHash
            || reader.GetFieldValue<byte array>(5) <> ticket.PayloadHash
            || reader.Read()
            || ticket.Sequence > receipt.CutoffSequence
        then
            corrupt ()

    let private pruned
        connection
        transaction
        (witness: WitnessProtocol)
        caseId
        (intent: MetadataRecord)
        (settled: MetadataRecord)
        =
        task {
            let! receipt = CaseTombstonePrunePrimaryRead.find connection transaction caseId

            let value = receipt |> Option.defaultWith corrupt

            if
                value.CaseId <> caseId
                || value.CandidateHash <> Security.Cryptography.SHA256.HashData(value.Canonical)
                || value.IntentSequence <= value.CutoffSequence
            then
                corrupt ()

            witnessProof (fun () ->
                witness.VerifyAuthorityEvidenceForCase(
                    value.EventId,
                    value.IntentSequence,
                    value.IntentEpoch,
                    value.IntentHash,
                    value.CandidateHash,
                    caseId
                ))

            target connection transaction value intent
            target connection transaction value settled
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        caseId
        operation
        sequence
        epoch
        (entryHash: byte array)
        (candidateDigest: byte array)
        settlement
        =
        task {
            if
                caseId = Guid.Empty
                || operation = Guid.Empty
                || sequence < 1L
                || sequence > cutoff
                || entryHash.Length <> 32
                || candidateDigest.Length <> 32
            then
                corrupt ()

            let intent = metadata witness cutoff caseId operation Intent
            let settled = metadata witness cutoff caseId operation settlement

            if
                intent.Ticket.Sequence <> sequence
                || intent.Ticket.Epoch <> epoch
                || intent.Ticket.EntryHash <> entryHash
                || settled.Ticket.Sequence <= sequence
            then
                corrupt ()

            match intent.PayloadPresent, settled.PayloadPresent with
            | true, true ->
                full witness settlement operation sequence epoch entryHash candidateDigest caseId
            | false, false -> do! pruned connection transaction witness caseId intent settled
            | _ -> corrupt ()
        }
