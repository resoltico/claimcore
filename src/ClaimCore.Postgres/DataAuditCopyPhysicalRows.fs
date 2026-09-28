namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal CopyPhysicalAuditRow =
    {
        EventId: Guid
        CopyId: Guid
        Revision: int64
        Proof: ManagedCopyPhysicalProof
        Canonical: byte array
        Signature: byte array
        ReportSha256: byte array
        MetadataMatches: bool
        EventKind: string
        EventCanonical: byte array
        EventWitnessSequence: int64
        EventWitnessEpoch: int64
        EventWitnessHash: byte array
        EventRecordedAt: DateTimeOffset
        ProducerKind: string
        CopyState: string
        CopyRevision: int64
        CopyProofSha256: byte array option
        CopyLastVerifiedAt: DateTimeOffset option
        PublicKey: byte array
        SignerPurpose: string
        SignerHolder: Guid
        SignerRegisteredSequence: int64
        SignerRetiredSequence: int64 option
        OriginalCanonical: byte array
        CopyAttestorHolder: Guid
    }

/// Fifty-row page with all joined projections materialized before nested historical reads.
module internal DataAuditCopyPhysicalRows =
    let private query =
        "SELECT v.verification_event_id,v.copy_id,v.copy_revision,"
        + "v.verifier_signing_key_id,v.verifier_holder_actor_id,v.nonce,"
        + "v.canonical_report,v.ed25519_signature,v.report_sha256,v.archive_object_id,"
        + "v.cluster_name,v.copy_kind,v.location_commitment,v.ciphertext_sha256,"
        + "v.ciphertext_bytes,v.decrypted_sha256,v.decrypted_bytes,v.postgres_system_id,"
        + "v.timeline,v.backup_manifest_sha256,v.wal_segment,v.recovered_row_count,"
        + "v.wal_segment_bytes,v.checked_at,v.valid_until,v.witness_cutoff_sequence,"
        + "v.witness_cutoff_hash,e.event_kind,e.canonical_attestation,e.witness_sequence,"
        + "e.witness_epoch,e.witness_entry_hash,e.recorded_at,c.producer_kind,c.state,"
        + "c.revision,c.verification_proof_sha256,c.last_verified_at,"
        + "s.ed25519_public_key,s.signer_purpose,s.holder_actor_id,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=s.signing_key_id AND r.revision=2),"
        + "original.canonical_attestation,attestor.holder_actor_id "
        + "FROM claimcore.managed_copy_verifications v "
        + "JOIN claimcore.managed_copy_events e ON e.event_id=v.verification_event_id "
        + "JOIN claimcore.managed_copies c ON c.copy_id=v.copy_id "
        + "JOIN claimcore.managed_copy_signers s "
        + "ON s.signing_key_id=v.verifier_signing_key_id "
        + "JOIN claimcore.managed_copy_events original "
        + "ON original.copy_id=v.copy_id AND original.revision=1 "
        + "JOIN claimcore.managed_copy_signers attestor "
        + "ON attestor.signing_key_id=original.signing_key_id "
        + "WHERE v.verification_event_id>@after "
        + "ORDER BY v.verification_event_id LIMIT 50"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private optional (reader: NpgsqlDataReader) index read =
        if reader.IsDBNull(index) then
            None
        else
            Some(read reader index)

    let private metadata
        (reader: NpgsqlDataReader)
        (proof: ManagedCopyPhysicalProof)
        (canonical: byte array)
        =
        reader.GetGuid(0) = proof.VerificationEventId
        && reader.GetGuid(1) = proof.CopyId
        && reader.GetInt64(2) = proof.CopyRevision
        && reader.GetGuid(3) = proof.VerifierSigningKeyId
        && reader.GetGuid(4) = proof.VerifierHolderActorId
        && bytes reader 5 = proof.Nonce
        && bytes reader 8 = Security.Cryptography.SHA256.HashData(canonical)
        && reader.GetGuid(9) = proof.ArchiveObjectId
        && reader.GetString(10) = proof.Cluster
        && reader.GetString(11) = proof.Kind
        && bytes reader 12 = proof.LocationCommitment
        && bytes reader 13 = proof.CiphertextSha256
        && reader.GetInt64(14) = proof.CiphertextBytes
        && bytes reader 15 = proof.DecryptedSha256
        && reader.GetInt64(16) = proof.DecryptedBytes
        && reader.GetString(17) = proof.PostgresSystemId
        && reader.GetInt32(18) = proof.Timeline
        && optional reader 19 bytes = proof.BackupManifestSha256
        && optional reader 20 (fun value index -> value.GetString(index)) = proof.WalSegment
        && optional reader 21 (fun value index -> value.GetInt64(index)) = proof.RecoveredRowCount
        && reader.GetInt32(22) = proof.WalSegmentBytes
        && reader.GetFieldValue<DateTimeOffset>(23) = proof.CheckedAt
        && reader.GetFieldValue<DateTimeOffset>(24) = proof.ValidUntil
        && reader.GetInt64(25) = proof.WitnessCutoffSequence
        && bytes reader 26 = proof.WitnessCutoffHash

    let private read (reader: NpgsqlDataReader) =
        let canonical = bytes reader 6

        let proof =
            ManagedCopyPhysicalProofCodec.parse canonical |> Option.defaultWith corrupt

        {
            EventId = reader.GetGuid(0)
            CopyId = reader.GetGuid(1)
            Revision = reader.GetInt64(2)
            Proof = proof
            Canonical = canonical
            Signature = bytes reader 7
            ReportSha256 = bytes reader 8
            MetadataMatches = metadata reader proof canonical
            EventKind = reader.GetString(27)
            EventCanonical = bytes reader 28
            EventWitnessSequence = reader.GetInt64(29)
            EventWitnessEpoch = reader.GetInt64(30)
            EventWitnessHash = bytes reader 31
            EventRecordedAt = reader.GetFieldValue<DateTimeOffset>(32)
            ProducerKind = reader.GetString(33)
            CopyState = reader.GetString(34)
            CopyRevision = reader.GetInt64(35)
            CopyProofSha256 = optional reader 36 bytes
            CopyLastVerifiedAt =
                optional reader 37 (fun value index -> value.GetFieldValue<DateTimeOffset>(index))
            PublicKey = bytes reader 38
            SignerPurpose = reader.GetString(39)
            SignerHolder = reader.GetGuid(40)
            SignerRegisteredSequence = reader.GetInt64(41)
            SignerRetiredSequence = optional reader 42 (fun value index -> value.GetInt64(index))
            OriginalCanonical = bytes reader 43
            CopyAttestorHolder = reader.GetGuid(44)
        }

    let page (connection: NpgsqlConnection) transaction after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<CopyPhysicalAuditRow>()

            while reader.Read() do
                rows.Add(read reader)

            return rows.ToArray()
        }
