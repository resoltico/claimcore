namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal WriterActivationAuditRow =
    {
        ActivationId: Guid
        Evidence: WriterActivationEvidence
        Canonical: byte array
        CandidateSha256: byte array
        IntentSequence: int64
        IntentHash: byte array
        SettlementSequence: int64
        SettlementEpoch: int64
        SettlementHash: byte array
        SignerPublicKey: byte array
        SignerPurpose: string
        SignerHolder: Guid
        SignerRegisteredSequence: int64
        SignerRetiredSequence: int64 option
        HandoffGeneration: int64
        HandoffSequence: int64
        HandoffHash: byte array
    }

module internal DataAuditWriterActivationRows =
    let private query =
        "SELECT a.activation_id,a.handoff_id,a.writer_generation,a.w1_sequence,a.w1_hash,"
        + "a.checkpoint_signing_key_id,a.checkpoint_holder_actor_id,a.report_sha256,"
        + "a.fence_sha256,a.supplement_sha256,a.final_wal_object_sha256,a.final_wal_object_count,"
        + "a.independent_probe_sha256,a.probe_evidence_sha256,a.signed_report,a.report_signature,"
        + "a.signed_fence,a.fence_signature,a.signed_supplement,a.supplement_signature,"
        + "a.valid_until,a.canonical_action,a.candidate_sha256,a.witness_intent_sequence,"
        + "a.witness_intent_hash,a.witness_sequence,a.witness_epoch,a.witness_entry_hash,"
        + "s.ed25519_public_key,s.signer_purpose,s.holder_actor_id,"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=1),"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=2),"
        + "h.new_generation,h.settlement_sequence,h.settlement_hash,"
        + "a.publication_manifest_sha256 "
        + "FROM claimcore.writer_activations a "
        + "JOIN claimcore.writer_handoffs h ON h.handoff_id=a.handoff_id "
        + "JOIN claimcore.managed_copy_signers s "
        + "ON s.signing_key_id=a.checkpoint_signing_key_id "
        + "WHERE a.writer_generation>@after ORDER BY a.writer_generation LIMIT 1"

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array>(index)

    let private evidence (identity: Identity) (reader: DbDataReader) : WriterActivationEvidence =
        {
            HandoffId = reader.GetGuid(1)
            InstallationId = identity.InstallationId
            LineageId = identity.LineageId
            Epoch = reader.GetInt64(26)
            WriterGeneration = reader.GetInt64(2)
            W1Sequence = reader.GetInt64(3)
            W1Hash = bytes reader 4
            PublicationManifestSha256 = bytes reader 36
            ReportSha256 = bytes reader 7
            FenceSha256 = bytes reader 8
            SupplementSha256 = bytes reader 9
            FinalWalObjectSha256 = bytes reader 10
            FinalWalObjectCount = reader.GetInt32(11)
            IndependentProbeSha256 = bytes reader 12
            ProbeEvidenceSha256 = bytes reader 13
            CheckpointSigningKeyId = reader.GetGuid(5)
            CheckpointHolderActorId = reader.GetGuid(6)
            SignedReport = bytes reader 14
            ReportSignature = bytes reader 15
            SignedFence = bytes reader 16
            FenceSignature = bytes reader 17
            SignedSupplement = bytes reader 18
            SupplementSignature = bytes reader 19
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(20)
        }

    let private row identity (reader: DbDataReader) : WriterActivationAuditRow =
        {
            ActivationId = reader.GetGuid(0)
            Evidence = evidence identity reader
            Canonical = bytes reader 21
            CandidateSha256 = bytes reader 22
            IntentSequence = reader.GetInt64(23)
            IntentHash = bytes reader 24
            SettlementSequence = reader.GetInt64(25)
            SettlementEpoch = reader.GetInt64(26)
            SettlementHash = bytes reader 27
            SignerPublicKey = bytes reader 28
            SignerPurpose = reader.GetString(29)
            SignerHolder = reader.GetGuid(30)
            SignerRegisteredSequence = reader.GetInt64(31)
            SignerRetiredSequence =
                if reader.IsDBNull(32) then
                    None
                else
                    Some(reader.GetInt64(32))
            HandoffGeneration = reader.GetInt64(33)
            HandoffSequence = reader.GetInt64(34)
            HandoffHash = bytes reader 35
        }

    let next connection transaction after identity (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync(ct)

            if not (reader.Read()) then
                return None
            else
                let value = row identity reader

                if reader.Read() then
                    corrupt ()

                return Some value
        }
