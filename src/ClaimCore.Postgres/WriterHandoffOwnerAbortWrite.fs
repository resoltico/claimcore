namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// A2: both signed owner decisions, one-use links and the primary abort ticket co-commit.
module internal WriterHandoffOwnerAbortWrite =
    let private approval
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        canonical
        signature
        digest
        (ticket: Ticket)
        approvalId
        signingKeyId
        actorId
        grantRevision
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.writer_handoff_abort_approvals "
                + "(approval_id,handoff_id,signing_key_id,owner_actor_id,owner_grant_revision,"
                + "abort_canonical,ed25519_signature,candidate_sha256,expires_at,"
                + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                + "(@approval,@handoff,@key,@actor,@revision,@canonical,@signature,@candidate,"
                + "@expires,@sequence,@epoch,@hash)",
                connection,
                transaction
            )

        Sql.uuid command "approval" approvalId
        Sql.uuid command "handoff" value.HandoffId
        Sql.uuid command "key" signingKeyId
        Sql.uuid command "actor" actorId
        Sql.integer command "revision" grantRevision
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "signature" NpgsqlDbType.Bytea (box signature)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box digest)
        Sql.add command "expires" NpgsqlDbType.TimestampTz (box value.ValidUntil)
        Sql.integer command "sequence" ticket.Sequence
        Sql.integer command "epoch" ticket.Epoch
        Sql.add command "hash" NpgsqlDbType.Bytea (box ticket.EntryHash)

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Abort approval was not retained."

    let private receipt
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        canonical
        digest
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.writer_handoff_aborts "
                + "(handoff_id,old_generation,approval_one_id,approval_two_id,"
                + "abort_canonical,abort_candidate_sha256,abort_sequence,abort_hash) VALUES "
                + "(@handoff,@generation,@first,@second,@canonical,@candidate,@sequence,@hash)",
                connection,
                transaction
            )

        Sql.uuid command "handoff" value.HandoffId
        Sql.integer command "generation" value.OldGeneration
        Sql.uuid command "first" value.ApprovalOneId
        Sql.uuid command "second" value.ApprovalTwoId
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box digest)
        Sql.integer command "sequence" ticket.Sequence
        Sql.add command "hash" NpgsqlDbType.Bytea (box ticket.EntryHash)

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Primary abort receipt was not retained."

    let private uses
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        =
        for approvalId in [ value.ApprovalOneId; value.ApprovalTwoId ] do
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.writer_handoff_abort_approval_uses "
                    + "(approval_id,handoff_id) VALUES (@approval,@handoff)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            Sql.uuid command "handoff" value.HandoffId

            if command.ExecuteNonQuery() <> 1 then
                invalidOp "Abort approval use was not retained."

    let private lineage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET "
                + "last_aborted_handoff_id=@handoff,last_aborted_handoff_sequence=@sequence,"
                + "last_aborted_handoff_hash=@hash "
                + "WHERE singleton AND writer_generation=@generation",
                connection,
                transaction
            )

        Sql.uuid command "handoff" value.HandoffId
        Sql.integer command "sequence" ticket.Sequence
        Sql.add command "hash" NpgsqlDbType.Bytea (box ticket.EntryHash)
        Sql.integer command "generation" value.OldGeneration

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Primary abort projection did not advance."

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        (ticket: Ticket)
        =
        let digest =
            WriterHandoffWitnessAbortCommands.candidate canonical signatureOne signatureTwo

        approval
            connection
            transaction
            value
            canonical
            signatureOne
            digest
            ticket
            value.ApprovalOneId
            value.AbortSigningKeyOneId
            value.OwnerOneActorId
            value.OwnerOneGrantRevision

        approval
            connection
            transaction
            value
            canonical
            signatureTwo
            digest
            ticket
            value.ApprovalTwoId
            value.AbortSigningKeyTwoId
            value.OwnerTwoActorId
            value.OwnerTwoGrantRevision

        receipt connection transaction value canonical digest ticket
        uses connection transaction value
        lineage connection transaction value ticket
