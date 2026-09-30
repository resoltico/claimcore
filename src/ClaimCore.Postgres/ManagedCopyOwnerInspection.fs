namespace ClaimCore.Postgres

open WitnessProtocolReconciliation

open System
open System.Data
open System.Data.Common
open System.Security.Cryptography
open System.Threading
open Npgsql

/// Bounded owner read of one retained REGISTER event; full dataset audit is separate.
module internal ManagedCopyOwnerInspection =
    let private query =
        "SELECT c.installation_id,c.lineage_id,c.witness_epoch,c.cluster_name,"
        + "c.copy_kind,c.ciphertext_sha256,c.ciphertext_bytes,c.encryption_key_id,"
        + "c.signing_key_id,c.custodian_commitment,c.location_commitment,"
        + "c.captured_at,c.retain_until,c.state,c.revision,c.event_hash,"
        + "e.event_id,e.canonical_attestation,e.ed25519_signature,"
        + "e.candidate_sha256,e.previous_hash,e.event_hash AS event_hash_e,"
        + "e.witness_sequence,e.witness_epoch AS event_epoch,e.witness_entry_hash,"
        + "s.ed25519_public_key,s.public_key_sha256,c.source_case_id,"
        + "c.verification_proof_sha256,s.signer_purpose "
        + "FROM claimcore.managed_copies c "
        + "JOIN claimcore.managed_copy_events e ON e.copy_id=c.copy_id "
        + "AND e.revision=1 AND e.producer_kind='OWNER_ATTESTED' "
        + "JOIN claimcore.managed_copy_signers s ON s.signing_key_id=c.signing_key_id "
        + "WHERE c.copy_id=@copy AND c.producer_kind='OWNER_ATTESTED' "
        + "AND c.revision=1 AND NOT EXISTS "
        + "(SELECT 1 FROM claimcore.managed_copy_events later "
        + "WHERE later.copy_id=c.copy_id AND later.revision>1)"

    let private exactProjection
        (witness: WitnessProtocol)
        copyId
        (value: ManagedCopyAttestation)
        canonical
        signature
        (reader: DbDataReader)
        =
        let bytes ordinal =
            reader.GetFieldValue<byte array>(ordinal)

        let eventHash =
            ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical (Some signature)

        let candidate = ManagedCopyAdministration.candidate value canonical signature

        try
            value.CopyId = copyId
            && value.InstallationId = reader.GetGuid(0)
            && value.LineageId = reader.GetGuid(1)
            && value.Epoch = reader.GetInt64(2)
            && value.Cluster = reader.GetString(3)
            && value.Kind = reader.GetString(4)
            && value.SourceCaseId =
                (if reader.IsDBNull(27) then
                     None
                 else
                     Some(reader.GetGuid(27)))
            && value.CiphertextSha256 = bytes 5
            && value.CiphertextBytes = reader.GetInt64(6)
            && value.EncryptionKeyId = reader.GetGuid(7)
            && value.SigningKeyId = reader.GetGuid(8)
            && value.CustodianCommitment = bytes 9
            && value.LocationCommitment = bytes 10
            && value.CapturedAt = reader.GetFieldValue<DateTimeOffset>(11)
            && value.RetainUntil = reader.GetFieldValue<DateTimeOffset>(12)
            && reader.GetString(13) = "UNVERIFIED"
            && reader.GetInt64(14) = 1L
            && reader.IsDBNull(28)
            && reader.GetString(29) = "COPY_ATTESTOR"
            && eventHash = bytes 15
            && value.EventId = reader.GetGuid(16)
            && SHA256.HashData(candidate) = bytes 19
            && bytes 20 = Array.zeroCreate<byte> 32
            && eventHash = bytes 21
            && value.InstallationId = witness.Identity.InstallationId
            && value.LineageId = witness.Identity.LineageId
            && value.Epoch = witness.Identity.Epoch
            && SHA256.HashData(bytes 25) = bytes 26
            && ManagedCopySignature.verify (bytes 25) canonical signature
        finally
            CryptographicOperations.ZeroMemory(candidate)

    let private verifyWitness
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        sequence
        epoch
        entryHash
        digest
        =
        witness.VerifyHistoricalTip(value.WitnessCutoffSequence, value.WitnessCutoffHash)
        witness.VerifyAuthorityEvidence(value.EventId, sequence, epoch, entryHash, digest)

    let inspect (connection: NpgsqlConnection) (witness: WitnessProtocol) copyId =
        task {
            if copyId = Guid.Empty then
                return false
            else
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    witness.Admit()

                    use! _authorityFence =
                        AuthorityOperationFence.acquireShared None connection CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

                    use readOnly =
                        new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

                    let! _ = readOnly.ExecuteNonQueryAsync()

                    use command = new NpgsqlCommand(query, connection, transaction)

                    Sql.uuid command "copy" copyId
                    let! rows = command.ExecuteReaderAsync()
                    use reader = rows
                    let! found = reader.ReadAsync()

                    if not found then
                        return false
                    else
                        let canonical = reader.GetFieldValue<byte array>(17)
                        let signature = reader.GetFieldValue<byte array>(18)
                        let parsed = ManagedCopyRegistrationAttestation.parse canonical

                        match parsed with
                        | None -> return false
                        | Some value ->
                            let bytes ordinal =
                                reader.GetFieldValue<byte array>(ordinal)

                            let sequence = reader.GetInt64(22)
                            let epoch = reader.GetInt64(23)
                            let entryHash = bytes 24
                            let digest = bytes 19

                            if
                                not (
                                    exactProjection witness copyId value canonical signature reader
                                )
                                || reader.Read()
                            then
                                return false
                            else
                                reader.Close()

                                verifyWitness witness value sequence epoch entryHash digest

                                do! transaction.CommitAsync()
                                return true
                with _ ->
                    return false
        }
