namespace ClaimCore.Database

open System
open Npgsql
open ClaimCore.Postgres

module internal DatabaseBackupHealthWriterFence =
    let verify
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claim: BackupHealthClaims)
        =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,writer_handoff_event_id,writer_handoff_sequence,"
                + "writer_handoff_hash,writer_activation_pending,writer_activation_sequence,"
                + "writer_activation_hash FROM claimcore.installation_lineage WHERE singleton",
                owner,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Backup health writer generation is unavailable."

        let optionalGuid index =
            if reader.IsDBNull(index) then
                None
            else
                Some(reader.GetGuid(index))

        let optionalLong index =
            if reader.IsDBNull(index) then
                None
            else
                Some(reader.GetInt64(index))

        let optionalHash index =
            if reader.IsDBNull(index) then
                None
            else
                Some(reader.GetFieldValue<byte array>(index) |> Convert.ToHexStringLower)

        let generation = reader.GetInt64(0)
        let handoff = optionalGuid 1
        let w1Sequence = optionalLong 2
        let w1Hash = optionalHash 3
        let pending = reader.GetBoolean(4)
        let activationSequence = optionalLong 5
        let activationHash = optionalHash 6

        if reader.Read() || pending || generation <> claim.WriterGeneration then
            invalidOp "Backup health writer activation is pending or divergent."

        let fence = claim.WriterFence

        if generation = 1L then
            if fence.Kind <> "GENESIS" || handoff.IsSome || w1Sequence.IsSome then
                invalidOp "Backup health genesis writer fence diverges."
        elif
            fence.Kind <> "HANDOFF"
            || fence.HandoffId <> handoff
            || fence.W1Sequence <> w1Sequence
            || fence.W1Hash <> w1Hash
            || fence.ActivationSequence <> activationSequence
            || fence.ActivationHash <> activationHash
        then
            invalidOp "Backup health W1/W2 writer fence diverges."
