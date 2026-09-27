namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredTerminalEvent =
    {
        EventId: Guid
        CaseId: Guid
        ActionName: string
        ResultingPhase: string
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        ActorAuthorityRevision: int64
        PreviousAuthorityRevision: int64
        PreviousAuthorityHash: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        CopyProofSha256: byte array
        CopyInventoryDigest: byte array
        RelevantCopyCount: int64
        WriterGeneration: int64
        PruneEventId: Guid
        RecoveryFenceDigest: byte array option
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        RecordedAt: DateTimeOffset
    }

module internal CaseTombstoneTerminalEventRead =
    let private eventRow (reader: System.Data.Common.DbDataReader) =
        {
            EventId = reader.GetGuid(0)
            CaseId = reader.GetGuid(1)
            ActionName = reader.GetString(2)
            ResultingPhase = reader.GetString(3)
            Canonical = reader.GetFieldValue<byte array>(4)
            CandidateHash = reader.GetFieldValue<byte array>(5)
            WitnessSequence = reader.GetInt64(6)
            WitnessEpoch = reader.GetInt64(7)
            WitnessHash = reader.GetFieldValue<byte array>(8)
            ApprovalOneId = reader.GetGuid(9)
            ApprovalTwoId = reader.GetGuid(10)
            ActorAuthorityRevision = reader.GetInt64(11)
            PreviousAuthorityRevision = reader.GetInt64(12)
            PreviousAuthorityHash = reader.GetFieldValue<byte array>(13)
            AuthorityRevision = reader.GetInt64(14)
            AuthorityHash = reader.GetFieldValue<byte array>(15)
            CopyProofSha256 = reader.GetFieldValue<byte array>(16)
            CopyInventoryDigest = reader.GetFieldValue<byte array>(17)
            RelevantCopyCount = reader.GetInt64(18)
            WriterGeneration = reader.GetInt64(19)
            PruneEventId = reader.GetGuid(20)
            RecoveryFenceDigest =
                if reader.IsDBNull(21) then
                    None
                else
                    Some(reader.GetFieldValue<byte array>(21))
            PolicyId = reader.GetString(22)
            SuppressionUntil = reader.GetFieldValue<DateTimeOffset>(23)
            RecordedAt = reader.GetFieldValue<DateTimeOffset>(24)
        }

    let find (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT terminal_event_id,case_id,action_name,resulting_phase,"
                    + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,"
                    + "witness_entry_hash,approval_one_id,approval_two_id,actor_authority_revision,"
                    + "previous_authority_revision,previous_authority_hash,authority_revision,"
                    + "authority_hash,copy_absence_seal_sha256,copy_inventory_digest,"
                    + "relevant_copy_count,writer_generation,prune_event_id,"
                    + "recovery_fence_digest,policy_id,suppression_until,recorded_at "
                    + "FROM claimcore.case_erasure_terminal_events "
                    + "WHERE terminal_event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value = eventRow reader

                if reader.Read() then
                    raise (InvalidDataException("Terminal owner event is duplicated."))

                return Some value
        }
