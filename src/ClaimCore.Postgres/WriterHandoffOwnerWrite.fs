namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Primary PREPARE target and both one-use approval rows co-commit after witness fencing.
module internal WriterHandoffOwnerWrite =
    let private uuid (command: NpgsqlCommand) name value =
        command.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value) |> ignore

    let private number (command: NpgsqlCommand) name value =
        command.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, value) |> ignore

    let private bytes (command: NpgsqlCommand) name value =
        command.Parameters.AddWithValue(name, NpgsqlDbType.Bytea, value) |> ignore

    let preparation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffPreparation)
        (canonical: byte array)
        (signature: byte array)
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.writer_handoff_preparations "
                + "(handoff_id,old_generation,new_generation,checkpoint_signing_key_id,"
                + "approval_one_id,approval_two_id,reviewed_cutoff_sequence,reviewed_cutoff_hash,"
                + "previous_sequence,previous_hash,new_capability_sha256,fence_report_sha256,"
                + "inventory_sha256,restore_report_sha256,canonical_action,ed25519_signature,"
                + "candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                + "(@handoff,@old,@new,@key,@first,@second,@reviewSeq,@reviewHash,"
                + "@previousSeq,@previousHash,@newCapability,@fence,@inventory,@report,"
                + "@canonical,@signature,@candidate,@witnessSeq,@epoch,@witnessHash)",
                connection,
                transaction
            )

        uuid command "handoff" value.HandoffId
        number command "old" value.OldGeneration
        number command "new" value.NewGeneration
        uuid command "key" value.CheckpointSigningKeyId
        uuid command "first" value.ApprovalOneId
        uuid command "second" value.ApprovalTwoId
        number command "reviewSeq" value.ReviewedCutoffSequence
        bytes command "reviewHash" value.ReviewedCutoffHash
        number command "previousSeq" value.ExpectedTipSequence
        bytes command "previousHash" value.ExpectedTipHash
        bytes command "newCapability" value.NewCapabilitySha256
        bytes command "fence" value.FenceReportSha256
        bytes command "inventory" value.InventorySha256
        bytes command "report" value.RestoreReportSha256
        bytes command "canonical" canonical
        bytes command "signature" signature
        bytes command "candidate" (SHA256.HashData(canonical))
        number command "witnessSeq" ticket.Sequence
        number command "epoch" ticket.Epoch
        bytes command "witnessHash" ticket.EntryHash

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Writer handoff primary preparation was not retained."

    let consume
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffPreparation)
        =
        for approvalId in [ value.ApprovalOneId; value.ApprovalTwoId ] do
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.writer_handoff_approval_uses "
                    + "(approval_id,handoff_id) VALUES (@approval,@handoff)",
                    connection,
                    transaction
                )

            uuid command "approval" approvalId
            uuid command "handoff" value.HandoffId

            if command.ExecuteNonQuery() <> 1 then
                invalidOp "Writer handoff approval use was not retained."

    let private bindSettlement
        (command: NpgsqlCommand)
        (prepared: WriterHandoffPreparation)
        (prepareCanonical: byte array)
        (prepareSignature: byte array)
        (prepareTicket: Ticket)
        (canonical: byte array)
        (signature: byte array)
        (ticket: Ticket)
        =
        uuid command "handoff" prepared.HandoffId
        number command "old" prepared.OldGeneration
        number command "new" prepared.NewGeneration
        uuid command "key" prepared.CheckpointSigningKeyId
        uuid command "first" prepared.ApprovalOneId
        uuid command "second" prepared.ApprovalTwoId
        bytes command "prepare" prepareCanonical
        bytes command "prepareSig" prepareSignature
        bytes command "prepareCandidate" (SHA256.HashData(prepareCanonical))
        number command "prepareSeq" prepareTicket.Sequence
        bytes command "prepareHash" prepareTicket.EntryHash
        bytes command "settlement" canonical
        bytes command "settlementSig" signature
        bytes command "settlementCandidate" (SHA256.HashData(canonical))
        number command "settlementSeq" ticket.Sequence
        bytes command "settlementHash" ticket.EntryHash

    let private advanceLineage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (prepared: WriterHandoffPreparation)
        (ticket: Ticket)
        =
        use lineage =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET writer_generation=@generation,"
                + "writer_handoff_event_id=@handoff,writer_handoff_sequence=@sequence,"
                + "writer_handoff_hash=@hash,writer_activation_pending=true,"
                + "writer_activation_event_id=NULL,writer_activation_sequence=NULL,"
                + "writer_activation_hash=NULL WHERE singleton AND writer_generation=@old",
                connection,
                transaction
            )

        number lineage "generation" prepared.NewGeneration
        number lineage "old" prepared.OldGeneration
        uuid lineage "handoff" prepared.HandoffId
        number lineage "sequence" ticket.Sequence
        bytes lineage "hash" ticket.EntryHash

        if lineage.ExecuteNonQuery() <> 1 then
            invalidOp "Writer generation primary projection did not advance."

    let settlement
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (prepared: WriterHandoffPreparation)
        (prepareCanonical: byte array)
        (prepareSignature: byte array)
        (prepareTicket: Ticket)
        (canonical: byte array)
        (signature: byte array)
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.writer_handoffs "
                + "(handoff_id,old_generation,new_generation,checkpoint_signing_key_id,"
                + "approval_one_id,approval_two_id,prepare_canonical,prepare_signature,"
                + "prepare_candidate_sha256,prepare_sequence,prepare_hash,"
                + "settlement_canonical,settlement_signature,settlement_candidate_sha256,"
                + "settlement_sequence,settlement_hash) VALUES "
                + "(@handoff,@old,@new,@key,@first,@second,@prepare,@prepareSig,"
                + "@prepareCandidate,@prepareSeq,@prepareHash,@settlement,@settlementSig,"
                + "@settlementCandidate,@settlementSeq,@settlementHash)",
                connection,
                transaction
            )

        bindSettlement
            command
            prepared
            prepareCanonical
            prepareSignature
            prepareTicket
            canonical
            signature
            ticket

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Writer handoff primary settlement was not retained."

        advanceLineage connection transaction prepared ticket
