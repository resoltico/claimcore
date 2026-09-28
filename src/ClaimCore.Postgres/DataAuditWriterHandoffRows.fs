namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql

[<NoEquality; NoComparison>]
type internal WriterHandoffAuditRow =
    {
        HandoffId: Guid
        OldGeneration: int64
        NewGeneration: int64
        CheckpointSigningKeyId: Guid
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        PrepareCanonical: byte array
        PrepareSignature: byte array
        PrepareCandidate: byte array
        PrepareSequence: int64
        PrepareHash: byte array
        SettlementCanonical: byte array
        SettlementSignature: byte array
        SettlementCandidate: byte array
        SettlementSequence: int64
        SettlementHash: byte array
        SignerPublicKey: byte array
        SignerPurpose: string
        SignerHolder: Guid
        SignerRegisteredSequence: int64
        SignerRetiredSequence: int64 option
    }

module internal DataAuditWriterHandoffRows =
    let private query =
        "SELECT h.handoff_id,h.old_generation,h.new_generation,h.checkpoint_signing_key_id,"
        + "h.approval_one_id,h.approval_two_id,h.prepare_canonical,h.prepare_signature,"
        + "h.prepare_candidate_sha256,h.prepare_sequence,h.prepare_hash,"
        + "h.settlement_canonical,h.settlement_signature,h.settlement_candidate_sha256,"
        + "h.settlement_sequence,h.settlement_hash,s.ed25519_public_key,s.signer_purpose,"
        + "s.holder_actor_id,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2) "
        + "FROM claimcore.writer_handoffs h "
        + "JOIN claimcore.managed_copy_signers s ON s.signing_key_id=h.checkpoint_signing_key_id "
        + "WHERE h.new_generation>@after ORDER BY h.new_generation LIMIT 50"

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array>(index)

    let page connection transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<WriterHandoffAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        HandoffId = reader.GetGuid(0)
                        OldGeneration = reader.GetInt64(1)
                        NewGeneration = reader.GetInt64(2)
                        CheckpointSigningKeyId = reader.GetGuid(3)
                        ApprovalOneId = reader.GetGuid(4)
                        ApprovalTwoId = reader.GetGuid(5)
                        PrepareCanonical = bytes reader 6
                        PrepareSignature = bytes reader 7
                        PrepareCandidate = bytes reader 8
                        PrepareSequence = reader.GetInt64(9)
                        PrepareHash = bytes reader 10
                        SettlementCanonical = bytes reader 11
                        SettlementSignature = bytes reader 12
                        SettlementCandidate = bytes reader 13
                        SettlementSequence = reader.GetInt64(14)
                        SettlementHash = bytes reader 15
                        SignerPublicKey = bytes reader 16
                        SignerPurpose = reader.GetString(17)
                        SignerHolder = reader.GetGuid(18)
                        SignerRegisteredSequence = reader.GetInt64(19)
                        SignerRetiredSequence =
                            if reader.IsDBNull(20) then
                                None
                            else
                                Some(reader.GetInt64(20))
                    }

            return rows.ToArray()
        }
