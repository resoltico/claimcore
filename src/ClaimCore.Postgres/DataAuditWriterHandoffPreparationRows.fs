namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql

[<NoEquality; NoComparison>]
type internal WriterHandoffPreparationAuditRow =
    {
        HandoffId: Guid
        OldGeneration: int64
        NewGeneration: int64
        CheckpointSigningKeyId: Guid
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        ReviewedCutoffSequence: int64
        ReviewedCutoffHash: byte array
        PreviousSequence: int64
        PreviousHash: byte array
        NewCapabilitySha256: byte array
        FenceReportSha256: byte array
        InventorySha256: byte array
        RestoreReportSha256: byte array
        Canonical: byte array
        Signature: byte array
        Candidate: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        RecordedAt: DateTimeOffset
        SignerPublicKey: byte array
        SignerPurpose: string
        SignerHolder: Guid
        SignerRegisteredSequence: int64
        SignerRetiredSequence: int64 option
    }

module internal DataAuditWriterHandoffPreparationRows =
    let private query =
        "SELECT p.handoff_id,p.old_generation,p.new_generation,p.checkpoint_signing_key_id,"
        + "p.approval_one_id,p.approval_two_id,p.reviewed_cutoff_sequence,"
        + "p.reviewed_cutoff_hash,p.previous_sequence,p.previous_hash,"
        + "p.new_capability_sha256,p.fence_report_sha256,p.inventory_sha256,"
        + "p.restore_report_sha256,p.canonical_action,p.ed25519_signature,"
        + "p.candidate_sha256,p.witness_sequence,p.witness_epoch,p.witness_entry_hash,"
        + "p.recorded_at,s.ed25519_public_key,s.signer_purpose,s.holder_actor_id,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2) "
        + "FROM claimcore.writer_handoff_preparations p "
        + "JOIN claimcore.managed_copy_signers s ON s.signing_key_id=p.checkpoint_signing_key_id "
        + "WHERE p.witness_sequence>@after ORDER BY p.witness_sequence LIMIT 50"

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array>(index)

    let page connection transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<WriterHandoffPreparationAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        HandoffId = reader.GetGuid(0)
                        OldGeneration = reader.GetInt64(1)
                        NewGeneration = reader.GetInt64(2)
                        CheckpointSigningKeyId = reader.GetGuid(3)
                        ApprovalOneId = reader.GetGuid(4)
                        ApprovalTwoId = reader.GetGuid(5)
                        ReviewedCutoffSequence = reader.GetInt64(6)
                        ReviewedCutoffHash = bytes reader 7
                        PreviousSequence = reader.GetInt64(8)
                        PreviousHash = bytes reader 9
                        NewCapabilitySha256 = bytes reader 10
                        FenceReportSha256 = bytes reader 11
                        InventorySha256 = bytes reader 12
                        RestoreReportSha256 = bytes reader 13
                        Canonical = bytes reader 14
                        Signature = bytes reader 15
                        Candidate = bytes reader 16
                        WitnessSequence = reader.GetInt64(17)
                        WitnessEpoch = reader.GetInt64(18)
                        WitnessHash = bytes reader 19
                        RecordedAt = reader.GetFieldValue<DateTimeOffset>(20)
                        SignerPublicKey = bytes reader 21
                        SignerPurpose = reader.GetString(22)
                        SignerHolder = reader.GetGuid(23)
                        SignerRegisteredSequence = reader.GetInt64(24)
                        SignerRetiredSequence =
                            if reader.IsDBNull(25) then
                                None
                            else
                                Some(reader.GetInt64(25))
                    }

            return rows.ToArray()
        }
