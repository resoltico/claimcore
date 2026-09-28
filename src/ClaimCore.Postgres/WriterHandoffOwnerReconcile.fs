namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Read-only exact proof of an already-committed witness handoff. Never appends W2.
module internal WriterHandoffOwnerReconcile =
    let primaryState connection transaction =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,writer_handoff_event_id,"
                + "writer_handoff_sequence,writer_handoff_hash "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
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

        if reader.Read() then
            invalidOp "Primary writer identity is ambiguous."

        generation, id, sequence, hash

    let verifyPrimary
        connection
        transaction
        (value: WriterHandoffSettlement)
        canonical
        signature
        (ticket: Ticket)
        =
        match WriterHandoffOwnerRead.completed connection transaction value.HandoffId with
        | Some(bytes, signed, sequence, hash) ->
            let generation, id, recorded, entryHash = primaryState connection transaction

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

            true
        | None ->
            let generation, id, sequence, hash = primaryState connection transaction

            let previous =
                if value.OldGeneration = 1L then
                    id.IsNone && sequence.IsNone && hash.IsNone
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
                        unbox<bool>(command.ExecuteScalar())
                    | _ -> false

            if generation <> value.OldGeneration || not previous then
                invalidOp "Primary writer generation cannot be backfilled."

            false

    let private signatureKey connection transaction keyId =
        use command =
            new NpgsqlCommand(
                "SELECT s.ed25519_public_key,s.signer_purpose,"
                + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
                + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
                + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
                + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2) "
                + "FROM claimcore.managed_copy_signers s WHERE s.signing_key_id=@key",
                connection,
                transaction
            )

        Sql.uuid command "key" keyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Historical checkpoint key is missing."

        let publicKey = reader.GetFieldValue<byte array>(0)
        let purpose = reader.GetString(1)
        let registered = reader.GetInt64(2)

        let retired =
            if reader.IsDBNull(3) then
                None
            else
                Some(reader.GetInt64(3))

        if reader.Read() || publicKey.Length <> 32 || purpose <> "CHECKPOINT" then
            invalidOp "Historical checkpoint key diverged."

        publicKey, registered, retired

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
        =
        if not (matchingSettlement prepared value canonical signature entry) then
            invalidOp "Witness handoff evidence differs."

        let sequence =
            entry.SettlementSequence
            |> Option.defaultWith (fun () -> invalidOp "No settlement.")

        let hash =
            entry.SettlementHash
            |> Option.defaultWith (fun () -> invalidOp "No settlement hash.")

        witness.VerifyHistoricalTip(sequence, hash)

        let intent =
            witness.EvidenceStore.TryReadEvidence(value.HandoffId, Intent)
            |> Option.defaultWith (fun () -> invalidOp "Handoff intent is absent.")

        let settled =
            witness.EvidenceStore.TryReadEvidence(value.HandoffId, SettledAuthority)
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

        settled.Ticket

    let trySettled
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        =
        match witness.EvidenceStore.TryReadHandoff(value.HandoffId) with
        | Some entry when entry.SettlementSequence.IsSome ->
            let publicKey, registered, retired =
                signatureKey connection transaction value.CheckpointSigningKeyId

            let settlementSequence = entry.SettlementSequence.Value

            if
                registered >= prepared.Intent.Sequence
                || (retired |> Option.exists (fun n -> n <= settlementSequence))
                || not (ManagedCopySignature.verify publicKey prepared.Canonical prepared.Signature)
                || not (ManagedCopySignature.verify publicKey canonical signature)
            then
                invalidOp "Historical checkpoint signature differs."

            let ticket = exactWitness witness prepared value canonical signature entry
            let current = witness.Snapshot()

            if
                current.HandoffPending
                || current.WriterGeneration <> value.NewGeneration
                || current.TipSequence < ticket.Sequence
            then
                invalidOp "Settled witness writer generation is unavailable."

            Some ticket
        | _ -> None
