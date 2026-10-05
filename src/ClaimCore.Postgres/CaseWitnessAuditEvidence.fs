namespace ClaimCore.Postgres

open System
open System.Threading
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
        ct
        =
        witnessProofAsync (fun () ->
            match settlement with
            | SettledAccepted ->
                witness.VerifyAcceptedEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId,
                    ct
                )
            | SettledRevoked ->
                witness.VerifyRevokedEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId,
                    ct
                )
            | SettledAuthority ->
                witness.VerifyAuthorityEvidenceForCase(
                    operation,
                    sequence,
                    epoch,
                    hash,
                    digest,
                    caseId,
                    ct
                )
            | _ -> corrupt ())

    let private metadata
        (witness: WitnessProtocol)
        cutoff
        caseId
        operation
        phase
        (ct: CancellationToken)
        =
        task {
            let! observed = witness.EvidenceStore.TryReadMetadataOperation(operation, phase, ct)
            let row = observed |> Option.defaultWith corrupt

            if
                row.Ticket.Sequence > cutoff
                || row.Ticket.Epoch <> witness.Identity.Epoch
                || row.Ticket.OperationId <> operation
                || row.Ticket.Phase <> phase
                || row.Ticket.ScopeKind <> Case
                || row.Ticket.SubjectCaseId <> Some caseId
            then
                corrupt ()

            return row
        }

    let private target
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (receipt: StoredWitnessPruneReceipt)
        (row: MetadataRecord)
        (ct: CancellationToken)
        =
        task {
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
            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                corrupt ()

            let ticket = row.Ticket

            let matches =
                not (
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
                    || ticket.Sequence > receipt.CutoffSequence
                )

            let! duplicated = reader.ReadAsync(ct)

            if not matches || duplicated then
                corrupt ()
        }

    let private pruned
        connection
        transaction
        (witness: WitnessProtocol)
        caseId
        (intent: MetadataRecord)
        (settled: MetadataRecord)
        (ct: CancellationToken)
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

            do!
                witnessProofAsync (fun () ->
                    witness.VerifyAuthorityEvidenceForCase(
                        value.EventId,
                        value.IntentSequence,
                        value.IntentEpoch,
                        value.IntentHash,
                        value.CandidateHash,
                        caseId,
                        ct
                    ))

            do! target connection transaction value intent ct
            do! target connection transaction value settled ct
        }

    let private requireProofIdentity
        cutoff
        caseId
        operation
        sequence
        (entryHash: byte array)
        (candidateDigest: byte array)
        =
        if
            caseId = Guid.Empty
            || operation = Guid.Empty
            || sequence < 1L
            || sequence > cutoff
            || entryHash.Length <> 32
            || candidateDigest.Length <> 32
        then
            corrupt ()

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
        (ct: CancellationToken)
        =
        task {
            requireProofIdentity cutoff caseId operation sequence entryHash candidateDigest

            let! intent = metadata witness cutoff caseId operation Intent ct
            let! settled = metadata witness cutoff caseId operation settlement ct

            if
                intent.Ticket.Sequence <> sequence
                || intent.Ticket.Epoch <> epoch
                || intent.Ticket.EntryHash <> entryHash
                || settled.Ticket.Sequence <= sequence
            then
                corrupt ()

            match intent.PayloadPresent, settled.PayloadPresent with
            | true, true ->
                do!
                    full
                        witness
                        settlement
                        operation
                        sequence
                        epoch
                        entryHash
                        candidateDigest
                        caseId
                        ct
            | false, false -> do! pruned connection transaction witness caseId intent settled ct
            | _ -> corrupt ()
        }
