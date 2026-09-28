namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type private OwnerCopyRegistrationProof =
    {
        Copy: ManagedCopyAttestation
        Sequence: int64
        Epoch: int64
        EntryHash: byte array
        CandidateDigest: byte array
    }

/// Read-only registration and complete signed transition replay under one fenced witness cutoff.
module internal DataAuditOwnerCopies =
    let private query =
        "SELECT c.copy_id,c.installation_id,c.lineage_id,c.witness_epoch,c.cluster_name,"
        + "c.copy_kind,c.source_case_id,c.postgres_system_id,c.timeline,c.wal_segment_bytes,"
        + "c.backup_manifest_sha256,c.wal_start_lsn,c.wal_end_lsn,c.wal_segment,"
        + "c.witness_cutoff_sequence,c.witness_cutoff_hash,c.ciphertext_sha256,c.ciphertext_bytes,"
        + "c.encryption_key_id,c.signing_key_id,c.custodian_commitment,c.location_commitment,"
        + "c.captured_at,c.retain_until,c.state,c.revision,c.event_hash,c.product_export_id,"
        + "c.last_verified_at,c.deletion_proof_sha256,e.event_id,e.canonical_attestation,"
        + "e.ed25519_signature,e.candidate_sha256,e.previous_hash,e.event_hash,"
        + "e.witness_sequence,e.witness_epoch,e.witness_entry_hash,e.signing_key_id,"
        + "e.event_kind,e.producer_kind,s.ed25519_public_key,s.public_key_sha256,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=c.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=c.signing_key_id AND r.revision=2) "
        + ",c.verification_proof_sha256,s.signer_purpose "
        + "FROM claimcore.managed_copies c JOIN claimcore.managed_copy_events e "
        + "ON e.copy_id=c.copy_id AND e.revision=1 JOIN claimcore.managed_copy_signers s "
        + "ON s.signing_key_id=c.signing_key_id WHERE c.producer_kind='OWNER_ATTESTED' "
        + "AND c.copy_id>@after ORDER BY c.copy_id LIMIT 50"

    let private optional (reader: NpgsqlDataReader) index getter =
        if reader.IsDBNull(index) then None else Some(getter index)

    let private identityMatches
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        (reader: NpgsqlDataReader)
        =
        value.CopyId = reader.GetGuid(0)
        && value.InstallationId = reader.GetGuid(1)
        && value.LineageId = reader.GetGuid(2)
        && value.Epoch = reader.GetInt64(3)
        && value.Cluster = reader.GetString(4)
        && value.Kind = reader.GetString(5)
        && value.SourceCaseId = optional reader 6 reader.GetGuid
        && value.InstallationId = witness.Identity.InstallationId
        && value.LineageId = witness.Identity.LineageId
        && value.Epoch = witness.Identity.Epoch

    let private archiveMatches (value: ManagedCopyAttestation) (reader: NpgsqlDataReader) =
        value.PostgresSystemId = optional reader 7 reader.GetString
        && value.Timeline = optional reader 8 reader.GetInt32
        && value.WalSegmentBytes = optional reader 9 reader.GetInt32
        && value.BackupManifestSha256 = optional reader 10 reader.GetFieldValue<byte array>
        && value.WalStartLsn = optional reader 11 reader.GetString
        && value.WalEndLsn = optional reader 12 reader.GetString
        && value.WalSegment = optional reader 13 reader.GetString
        && value.WitnessCutoffSequence = reader.GetInt64(14)
        && value.WitnessCutoffHash = reader.GetFieldValue<byte array>(15)

    let private custodyMatches (value: ManagedCopyAttestation) (reader: NpgsqlDataReader) =
        value.CiphertextSha256 = reader.GetFieldValue<byte array>(16)
        && value.CiphertextBytes = reader.GetInt64(17)
        && value.EncryptionKeyId = reader.GetGuid(18)
        && value.SigningKeyId = reader.GetGuid(19)
        && value.CustodianCommitment = reader.GetFieldValue<byte array>(20)
        && value.LocationCommitment = reader.GetFieldValue<byte array>(21)
        && value.CapturedAt = reader.GetFieldValue<DateTimeOffset>(22)
        && value.RetainUntil = reader.GetFieldValue<DateTimeOffset>(23)
        && reader.GetInt64(25) >= 1L

    let private eventMatches
        (value: ManagedCopyAttestation)
        (reader: NpgsqlDataReader)
        canonical
        signature
        =
        let eventHash =
            ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical (Some signature)

        value.EventId = reader.GetGuid(30)
        && value.SigningKeyId = reader.GetGuid(39)
        && reader.GetString(40) = "REGISTER"
        && reader.GetString(41) = "OWNER_ATTESTED"
        && eventHash = reader.GetFieldValue<byte array>(35)
        && reader.GetFieldValue<byte array>(34) = Array.zeroCreate<byte> 32
        && SHA256.HashData(reader.GetFieldValue<byte array>(42)) =
            reader.GetFieldValue<byte array>(43)
        && reader.GetInt64(36) > reader.GetInt64(44)
        && (reader.IsDBNull(45) || reader.GetInt64(36) < reader.GetInt64(45))
        && ManagedCopySignature.verify (reader.GetFieldValue<byte array>(42)) canonical signature
        && reader.GetString(47) = "COPY_ATTESTOR"

    let private witnessRegistration
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (proof: OwnerCopyRegistrationProof)
        =
        task {
            let value = proof.Copy

            witnessProof (fun () ->
                witness.VerifyHistoricalTip(value.WitnessCutoffSequence, value.WitnessCutoffHash))

            match value.SourceCaseId with
            | Some caseId ->
                do!
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        value.EventId
                        proof.Sequence
                        proof.Epoch
                        proof.EntryHash
                        proof.CandidateDigest
                        SettledAuthority
            | None ->
                witnessProof (fun () ->
                    witness.VerifyAuthorityEvidenceForInstallation(
                        value.EventId,
                        proof.Sequence,
                        proof.Epoch,
                        proof.EntryHash,
                        proof.CandidateDigest
                    ))
        }

    let private projection
        (value: ManagedCopyAttestation)
        canonical
        signature
        (reader: NpgsqlDataReader)
        =
        {
            CopyId = value.CopyId
            Registered = value
            RegistrationHash =
                ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical (Some signature)
            State = reader.GetString(24)
            Revision = reader.GetInt64(25)
            EventHash = reader.GetFieldValue<byte array>(26)
            VerificationProofSha256 = optional reader 46 reader.GetFieldValue<byte array>
            LastVerifiedAt = optional reader 28 reader.GetFieldValue<DateTimeOffset>
            DeletionProofSha256 = optional reader 29 reader.GetFieldValue<byte array>
        }

    let private verifyRow (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) =
        let canonical = reader.GetFieldValue<byte array>(31)
        let signature = reader.GetFieldValue<byte array>(32)

        let value =
            ManagedCopyRegistrationAttestation.parse canonical |> Option.defaultWith corrupt

        let candidate = ManagedCopyAdministration.candidate value canonical signature

        try
            if
                not (identityMatches witness value reader)
                || not (archiveMatches value reader)
                || not (custodyMatches value reader)
                || not (eventMatches value reader canonical signature)
                || SHA256.HashData(candidate) <> reader.GetFieldValue<byte array>(33)
                || reader.GetInt64(36) > cutoff
                || reader.GetInt64(37) <> witness.Identity.Epoch
            then
                corrupt ()

            projection value canonical signature reader,
            {
                Copy = value
                Sequence = reader.GetInt64(36)
                Epoch = reader.GetInt64(37)
                EntryHash = reader.GetFieldValue<byte array>(38)
                CandidateDigest = reader.GetFieldValue<byte array>(33)
            }
        finally
            CryptographicOperations.ZeroMemory(candidate)

    let private page connection transaction witness cutoff after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "after" after
            let projections = ResizeArray<OwnerCopyProjection * OwnerCopyRegistrationProof>()

            let! _ =
                task {
                    use! reader = command.ExecuteReaderAsync(ct)

                    while reader.Read() do
                        let projection, proof = verifyRow witness cutoff reader

                        if
                            projections.Count > 0
                            && (fst projections[projections.Count - 1]).CopyId = projection.CopyId
                        then
                            corrupt ()

                        projections.Add(projection, proof)
                }

            for projection, proof in projections do
                do! witnessRegistration connection transaction witness cutoff proof

                do!
                    DataAuditOwnerCopyTransitions.verify
                        connection
                        transaction
                        witness
                        cutoff
                        projection
                        ct

            return
                (if projections.Count = 0 then
                     None
                 else
                     Some((fst projections[projections.Count - 1]).CopyId)),
                int64 projections.Count
        }

    let private storedCount connection transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.managed_copies WHERE producer_kind='OWNER_ATTESTED'",
                    connection,
                    transaction
                )

            let! value = command.ExecuteScalarAsync(ct)
            return unbox<int64> value
        }

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! last, pageCount = page connection transaction witness cutoff after ct
                count <- count + pageCount

                match last with
                | None -> more <- false
                | Some copyId -> after <- copyId

            let! stored = storedCount connection transaction ct

            if count <> stored then
                corrupt ()

            return count
        }
