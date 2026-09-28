namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes

/// Primary projection of the exact independently settled activation ticket.
module internal WriterActivationPrimary =
    let private insertSql =
        "INSERT INTO claimcore.writer_activations "
        + "(activation_id,handoff_id,writer_generation,w1_sequence,w1_hash,"
        + "checkpoint_signing_key_id,checkpoint_holder_actor_id,publication_manifest_sha256,report_sha256,"
        + "fence_sha256,supplement_sha256,final_wal_object_sha256,final_wal_object_count,"
        + "independent_probe_sha256,probe_evidence_sha256,signed_report,report_signature,"
        + "signed_fence,fence_signature,signed_supplement,supplement_signature,valid_until,"
        + "canonical_action,candidate_sha256,witness_intent_sequence,witness_intent_hash,"
        + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
        + "(@activation,@handoff,@generation,@w1sequence,@w1hash,@key,@holder,@publicationManifest,@report,"
        + "@fence,@supplement,@objects,@count,@probe,@probeEvidence,@signedReport,"
        + "@reportSignature,@signedFence,@fenceSignature,@signedSupplement,"
        + "@supplementSignature,@validUntil,@canonical,@candidate,@intentSequence,"
        + "@intentHash,@settlementSequence,@epoch,@settlementHash)"

    let private bindEvidence (command: NpgsqlCommand) (value: WriterActivationEvidence) =
        Sql.uuid command "handoff" value.HandoffId
        Sql.integer command "generation" value.WriterGeneration
        Sql.integer command "w1sequence" value.W1Sequence
        Sql.add command "w1hash" NpgsqlDbType.Bytea (box value.W1Hash)
        Sql.uuid command "key" value.CheckpointSigningKeyId
        Sql.uuid command "holder" value.CheckpointHolderActorId

        Sql.add
            command
            "publicationManifest"
            NpgsqlDbType.Bytea
            (box value.PublicationManifestSha256)

        Sql.add command "report" NpgsqlDbType.Bytea (box value.ReportSha256)
        Sql.add command "fence" NpgsqlDbType.Bytea (box value.FenceSha256)
        Sql.add command "supplement" NpgsqlDbType.Bytea (box value.SupplementSha256)
        Sql.add command "objects" NpgsqlDbType.Bytea (box value.FinalWalObjectSha256)
        Sql.integer command "count" value.FinalWalObjectCount
        Sql.add command "probe" NpgsqlDbType.Bytea (box value.IndependentProbeSha256)
        Sql.add command "probeEvidence" NpgsqlDbType.Bytea (box value.ProbeEvidenceSha256)
        Sql.add command "signedReport" NpgsqlDbType.Bytea (box value.SignedReport)
        Sql.add command "reportSignature" NpgsqlDbType.Bytea (box value.ReportSignature)
        Sql.add command "signedFence" NpgsqlDbType.Bytea (box value.SignedFence)
        Sql.add command "fenceSignature" NpgsqlDbType.Bytea (box value.FenceSignature)
        Sql.add command "signedSupplement" NpgsqlDbType.Bytea (box value.SignedSupplement)
        Sql.add command "supplementSignature" NpgsqlDbType.Bytea (box value.SupplementSignature)
        Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box value.ValidUntil)

    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterActivationEvidence)
        activationId
        (canonical: byte array)
        (tickets: WriterActivationTickets)
        =
        use command = new NpgsqlCommand(insertSql, connection, transaction)
        Sql.uuid command "activation" activationId
        bindEvidence command value
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box (SHA256.HashData(canonical)))
        Sql.integer command "intentSequence" tickets.Intent.Sequence
        Sql.add command "intentHash" NpgsqlDbType.Bytea (box tickets.Intent.EntryHash)
        Sql.integer command "settlementSequence" tickets.Settlement.Sequence
        Sql.integer command "epoch" tickets.Settlement.Epoch
        Sql.add command "settlementHash" NpgsqlDbType.Bytea (box tickets.Settlement.EntryHash)

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Writer activation primary event was not retained."

    let release
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterActivationEvidence)
        activationId
        (tickets: WriterActivationTickets)
        =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET writer_activation_pending=false,"
                + "writer_activation_event_id=@activation,writer_activation_sequence=@sequence,"
                + "writer_activation_hash=@hash WHERE singleton AND writer_activation_pending "
                + "AND writer_generation=@generation AND writer_handoff_event_id=@handoff "
                + "AND writer_handoff_sequence=@w1sequence AND writer_handoff_hash=@w1hash",
                connection,
                transaction
            )

        Sql.uuid command "activation" activationId
        Sql.integer command "sequence" tickets.Settlement.Sequence
        Sql.add command "hash" NpgsqlDbType.Bytea (box tickets.Settlement.EntryHash)
        Sql.integer command "generation" value.WriterGeneration
        Sql.uuid command "handoff" value.HandoffId
        Sql.integer command "w1sequence" value.W1Sequence
        Sql.add command "w1hash" NpgsqlDbType.Bytea (box value.W1Hash)

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Writer activation primary projection did not advance."

    let requireExisting
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        activationId
        (canonical: byte array)
        (tickets: WriterActivationTickets)
        =
        use command =
            new NpgsqlCommand(
                "SELECT canonical_action,candidate_sha256,witness_intent_sequence,"
                + "witness_intent_hash,witness_sequence,witness_epoch,witness_entry_hash "
                + "FROM claimcore.writer_activations WHERE activation_id=@activation",
                connection,
                transaction
            )

        Sql.uuid command "activation" activationId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer activation primary event is absent."

        let candidate = SHA256.HashData(canonical)

        let matching =
            reader.GetFieldValue<byte array>(0) = canonical
            && reader.GetFieldValue<byte array>(1) = candidate
            && reader.GetInt64(2) = tickets.Intent.Sequence
            && reader.GetFieldValue<byte array>(3) = tickets.Intent.EntryHash
            && reader.GetInt64(4) = tickets.Settlement.Sequence
            && reader.GetInt64(5) = tickets.Settlement.Epoch
            && reader.GetFieldValue<byte array>(6) = tickets.Settlement.EntryHash

        if reader.Read() || not matching then
            invalidOp "Writer activation primary replay diverged."

    let current (connection: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,writer_activation_pending,writer_activation_event_id,"
                + "writer_activation_sequence,writer_activation_hash "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Writer activation primary projection is absent."

        let optional index read =
            if reader.IsDBNull(index) then None else Some(read index)

        let state =
            {
                Generation = reader.GetInt64(0)
                Pending = reader.GetBoolean(1)
                ActivationId = optional 2 reader.GetGuid
                ActivationSequence = optional 3 reader.GetInt64
                ActivationHash = optional 4 reader.GetFieldValue<byte array>
            }

        if reader.Read() then
            invalidOp "Writer activation primary projection is duplicated."

        state
