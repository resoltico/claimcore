namespace ClaimCore.Witness

open System
open System.Threading
open System.Data.Common
open Npgsql
open NpgsqlTypes

module internal WitnessStoreHandoff =
    let private query =
        "SELECT h.handoff_id,h.old_generation,h.new_generation,h.previous_sequence,h.previous_hash,"
        + "new_capability_sha256,checkpoint_signing_key_id,prepare_canonical,"
        + "prepare_signature,approval_one_id,approval_two_id,prepare_candidate_sha256,"
        + "prepare_sequence,prepare_hash,settlement_candidate_sha256,settlement_sequence,"
        + "settlement_hash,settlement_canonical,settlement_signature,"
        + "abort_candidate_sha256,abort_sequence,abort_hash,abort_canonical,"
        + "abort_signature_one,abort_signature_two,abort_signing_key_one,"
        + "abort_signing_key_two,j.recorded_at,aborted.recorded_at "
        + "FROM claimcore_witness.writer_handoffs h "
        + "JOIN claimcore_witness.installation i ON i.singleton "
        + "JOIN claimcore_witness.journal j ON j.installation_id=i.installation_id "
        + "AND j.sequence=h.prepare_sequence AND j.operation_id=h.handoff_id "
        + "AND j.phase='INTENT' "
        + "LEFT JOIN claimcore_witness.journal aborted "
        + "ON aborted.installation_id=i.installation_id "
        + "AND aborted.sequence=h.abort_sequence AND aborted.operation_id=h.handoff_id "
        + "AND aborted.phase='ABORTED_BEFORE_COMMIT' "
        + "WHERE h.handoff_id=@handoff"

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array>(index)

    let private optional (reader: DbDataReader) index read =
        if reader.IsDBNull(index) then
            None
        else
            Some(read reader index)

    let private decoded (reader: DbDataReader) =
        {
            HandoffId = reader.GetGuid(0)
            OldGeneration = reader.GetInt64(1)
            NewGeneration = reader.GetInt64(2)
            PreviousSequence = reader.GetInt64(3)
            PreviousHash = bytes reader 4
            NewCapabilitySha256 = bytes reader 5
            CheckpointSigningKeyId = reader.GetGuid(6)
            PrepareCanonical = bytes reader 7
            PrepareSignature = bytes reader 8
            ApprovalOneId = reader.GetGuid(9)
            ApprovalTwoId = reader.GetGuid(10)
            PrepareCandidateSha256 = bytes reader 11
            PrepareSequence = reader.GetInt64(12)
            PrepareHash = bytes reader 13
            PrepareRecordedAt = reader.GetFieldValue<DateTimeOffset>(27)
            SettlementCandidateSha256 = optional reader 14 bytes
            SettlementSequence = optional reader 15 (fun value index -> value.GetInt64(index))
            SettlementHash = optional reader 16 bytes
            SettlementCanonical = optional reader 17 bytes
            SettlementSignature = optional reader 18 bytes
            AbortCandidateSha256 = optional reader 19 bytes
            AbortSequence = optional reader 20 (fun value index -> value.GetInt64(index))
            AbortHash = optional reader 21 bytes
            AbortCanonical = optional reader 22 bytes
            AbortSignatureOne = optional reader 23 bytes
            AbortSignatureTwo = optional reader 24 bytes
            AbortSigningKeyOne = optional reader 25 (fun value index -> value.GetGuid(index))
            AbortSigningKeyTwo = optional reader 26 (fun value index -> value.GetGuid(index))
            AbortRecordedAt =
                optional reader 28 (fun value index -> value.GetFieldValue<DateTimeOffset>(index))
        }

    let read writerConnection (identity: Identity) handoffId (ct: CancellationToken) =
        task {
            if handoffId = Guid.Empty then
                invalidArg (nameof handoffId) "Writer handoff identity is invalid."

            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct
            use command = new NpgsqlCommand(query, connection)

            command.Parameters.AddWithValue("handoff", NpgsqlDbType.Uuid, handoffId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let result = decoded reader

                let! duplicated = reader.ReadAsync(ct)

                if duplicated then
                    invalidOp "Writer handoff evidence is duplicated."

                return Some result
        }
