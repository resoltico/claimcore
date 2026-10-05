namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Read-only exact proof of an already-committed witness handoff. Never appends W2.
module internal WriterHandoffOwnerReconcile =
    let primaryState connection transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT writer_generation,writer_handoff_event_id,"
                    + "writer_handoff_sequence,writer_handoff_hash "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Primary writer identity is absent."

            let generation = reader.GetInt64(0)
            let id = if reader.IsDBNull(1) then None else Some(reader.GetGuid(1))

            let sequence =
                if reader.IsDBNull(2) then
                    None
                else
                    Some(reader.GetInt64(2))

            let hash =
                if reader.IsDBNull(3) then
                    None
                else
                    Some(reader.GetFieldValue<byte array>(3))

            let! extra = reader.ReadAsync(ct)

            if extra then
                invalidOp "Primary writer identity is ambiguous."

            return generation, id, sequence, hash
        }

    let private priorGeneration
        connection
        transaction
        (value: WriterHandoffSettlement)
        (id: Guid option)
        (sequence: int64 option)
        (hash: byte array option)
        ct
        =
        task {
            if value.OldGeneration = 1L then
                return id.IsNone && sequence.IsNone && hash.IsNone
            else
                match id, sequence, hash with
                | Some priorId, Some priorSequence, Some priorHash ->
                    use command =
                        new NpgsqlCommand(
                            "SELECT EXISTS (SELECT 1 FROM claimcore.writer_handoffs "
                            + "WHERE handoff_id=@handoff AND new_generation=@generation "
                            + "AND settlement_sequence=@sequence AND settlement_hash=@hash)",
                            connection,
                            transaction
                        )

                    Sql.uuid command "handoff" priorId
                    Sql.integer command "generation" value.OldGeneration
                    Sql.integer command "sequence" priorSequence

                    Sql.add command "hash" NpgsqlTypes.NpgsqlDbType.Bytea (box priorHash)

                    let! result = command.ExecuteScalarAsync(ct)
                    return unbox<bool> result
                | _ -> return false
        }

    let verifyPrimary
        connection
        transaction
        (value: WriterHandoffSettlement)
        canonical
        signature
        (ticket: Ticket)
        (ct: CancellationToken)
        =
        task {
            let! completed =
                WriterHandoffOwnerRead.completed connection transaction value.HandoffId ct

            match completed with
            | Some(bytes, signed, sequence, hash) ->
                let! generation, id, recorded, entryHash = primaryState connection transaction ct

                if
                    bytes <> canonical
                    || signed <> signature
                    || sequence <> ticket.Sequence
                    || hash <> ticket.EntryHash
                    || generation <> value.NewGeneration
                    || id <> Some value.HandoffId
                    || recorded <> Some ticket.Sequence
                    || entryHash <> Some ticket.EntryHash
                then
                    invalidOp "Primary writer settlement differs."

                return true
            | None ->
                let! generation, id, sequence, hash = primaryState connection transaction ct

                let! previous = priorGeneration connection transaction value id sequence hash ct

                if generation <> value.OldGeneration || not previous then
                    invalidOp "Primary writer generation cannot be backfilled."

                return false
        }

    let private checkCiphertext
        (witness: WitnessProtocol)
        handoffId
        phase
        (evidence: Evidence)
        expected
        =
        let plain =
            witness.KeyCustody.Decrypt(
                evidence.Ticket.KeyId,
                witness.AssociatedData(handoffId, phase),
                evidence.EncryptedPayload
            )

        try
            if plain <> expected then
                invalidOp "Handoff ciphertext differs."
        finally
            CryptographicOperations.ZeroMemory(plain)

    let private matchingSettlement
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        (entry: WriterHandoffEvidence)
        =
        entry.HandoffId = value.HandoffId
        && entry.OldGeneration = value.OldGeneration
        && entry.NewGeneration = value.NewGeneration
        && entry.PrepareCanonical = prepared.Canonical
        && entry.PrepareSignature = prepared.Signature
        && entry.PrepareSequence = prepared.Intent.Sequence
        && entry.PrepareHash = prepared.Intent.EntryHash
        && entry.SettlementCanonical = Some canonical
        && entry.SettlementSignature = Some signature
        && entry.SettlementCandidateSha256 = Some(SHA256.HashData(canonical))
        && entry.NewCapabilitySha256 = value.NewCapabilitySha256

    let private exactWitness
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        (entry: WriterHandoffEvidence)
        (ct: CancellationToken)
        =
        task {
            if not (matchingSettlement prepared value canonical signature entry) then
                invalidOp "Witness handoff evidence differs."

            let sequence =
                entry.SettlementSequence
                |> Option.defaultWith (fun () -> invalidOp "No settlement.")

            let hash =
                entry.SettlementHash
                |> Option.defaultWith (fun () -> invalidOp "No settlement hash.")

            do! witness.VerifyHistoricalTip(sequence, hash, ct)

            let! retainedIntent =
                witness.EvidenceStore.TryReadEvidence(value.HandoffId, Intent, ct)

            let intent =
                retainedIntent
                |> Option.defaultWith (fun () -> invalidOp "Handoff intent is absent.")

            let! retainedSettled =
                witness.EvidenceStore.TryReadEvidence(value.HandoffId, SettledAuthority, ct)

            let settled =
                retainedSettled
                |> Option.defaultWith (fun () -> invalidOp "Handoff settlement is absent.")

            if
                intent.Ticket.Sequence <> prepared.Intent.Sequence
                || intent.Ticket.EntryHash <> prepared.Intent.EntryHash
                || settled.Ticket.Sequence <> sequence
                || settled.Ticket.EntryHash <> hash
                || intent.Ticket.ScopeKind <> Installation
                || settled.Ticket.ScopeKind <> Installation
            then
                invalidOp "Witness handoff tickets differ."

            checkCiphertext witness value.HandoffId "INTENT" intent prepared.Canonical
            let expected = WriterHandoffEvidenceHash.settlement prepared.Canonical canonical

            try
                checkCiphertext witness value.HandoffId "SETTLED_AUTHORITY" settled expected
            finally
                CryptographicOperations.ZeroMemory(expected)

            return settled.Ticket
        }

    let trySettled
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        (ct: CancellationToken)
        =
        task {
            let! stored = witness.EvidenceStore.TryReadHandoff(value.HandoffId, ct)

            match stored with
            | Some entry when entry.SettlementSequence.IsSome ->
                let publicKey, registered, retired =
                    WriterHandoffOwnerChecks.historicalCheckpointKey
                        connection
                        transaction
                        value.CheckpointSigningKeyId

                let settlementSequence = entry.SettlementSequence.Value

                if
                    registered >= prepared.Intent.Sequence
                    || (retired |> Option.exists (fun n -> n <= settlementSequence))
                    || not (
                        ManagedCopySignature.verify publicKey prepared.Canonical prepared.Signature
                    )
                    || not (ManagedCopySignature.verify publicKey canonical signature)
                then
                    invalidOp "Historical checkpoint signature differs."

                let! ticket = exactWitness witness prepared value canonical signature entry ct
                let! current = witness.Snapshot(ct)

                if
                    current.HandoffPending
                    || current.WriterGeneration <> value.NewGeneration
                    || current.TipSequence < ticket.Sequence
                then
                    invalidOp "Settled witness writer generation is unavailable."

                return Some ticket
            | _ -> return None
        }
