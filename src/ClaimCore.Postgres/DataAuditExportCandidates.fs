namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open ClaimCore.RecordFormat
open DataAuditCommon

/// Exact export candidate proof before and after authorized live payload deletion.
module internal DataAuditExportCandidates =
    let private storedEvidence (reader: NpgsqlDataReader) artifactBytes =
        let ordinal name = reader.GetOrdinal(name)
        let uuid name = reader.GetGuid(ordinal name)
        let number name = reader.GetInt64(ordinal name)

        let bytes name =
            reader.GetFieldValue<byte array>(ordinal name)

        let importer =
            if reader.IsDBNull(ordinal "importer_actor_id") then
                None
            else
                Some(uuid "importer_actor_id")

        let artifact: RecoveryArtifactV3 =
            {
                KeyId = uuid "key_id"
                ExportId = uuid "export_id"
                InstallationId = uuid "export_installation"
                Epoch = number "witness_epoch"
                CaseId = uuid "case_id"
                PreparerActorId = uuid "preparer_actor_id"
                PreparerGrantRevision = number "preparer_grant_revision"
                ImporterActorId = importer
                ExporterActorId = uuid "exporter_actor_id"
                ExporterGrantRevision = number "exporter_grant_revision"
                OperationId = uuid "operation_id"
                IssuedAt = reader.GetFieldValue<DateTimeOffset>(ordinal "issued_at")
                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(ordinal "expires_at")
                CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
                RequestFingerprintVersion = RecordVersions.RequestFingerprint
                Nonce = bytes "nonce"
                CanonicalRequest = Array.empty
            }

        {
            ExportId = uuid "export_id"
            Artifact = artifact
            ExporterActorId = uuid "exporter_actor_id"
            ExporterGrantRevision = number "exporter_grant_revision"
            ArtifactBytes = artifactBytes
            KeyIssuanceOrdinal = reader.GetInt32(ordinal "key_issuance_ordinal")
            KeyMaximumExports = reader.GetInt32(ordinal "key_max_exports")
        }

    let exactCandidate (reader: NpgsqlDataReader) =
        let ordinal name = reader.GetOrdinal(name)

        let bytes name =
            reader.GetFieldValue<byte array>(ordinal name)

        let uuid name = reader.GetGuid(ordinal name)
        let canonical = bytes "canonical_action"
        let artifact = bytes "artifact_bytes"
        let digest = bytes "witness_candidate_sha256"
        let artifactDigest = bytes "artifact_sha256"

        if
            SHA256.HashData(canonical) <> digest
            || SHA256.HashData(artifact) <> artifactDigest
            || not (
                RecoveryArtifactExportCandidate.verifyStored
                    (storedEvidence reader artifact)
                    canonical
            )
        then
            corrupt ()

        witnessProof (fun () ->
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
            let root = document.RootElement

            if
                root.GetProperty("kind").GetString() <> "RECOVERY_EXPORT_V4"
                || root.GetProperty("exportId").GetGuid() <> uuid "export_id"
                || root.GetProperty("caseId").GetGuid() <> uuid "case_id"
                || root.GetProperty("keyId").GetGuid() <> uuid "key_id"
                || root.GetProperty("artifactSha256").GetString()
                   <> Convert.ToHexStringLower(artifactDigest)
                || root.GetProperty("artifactBytes").GetInt64() <> int64 artifact.Length
            then
                corrupt ())

        canonical, artifactDigest, digest

    let purgedCandidate (reader: NpgsqlDataReader) =
        let ordinal name = reader.GetOrdinal(name)

        let bytes name =
            reader.GetFieldValue<byte array>(ordinal name)

        let canonical = bytes "canonical_attestation"
        let digest = bytes "witness_candidate_sha256"
        let artifactDigest = bytes "artifact_sha256"

        let artifactLength = reader.GetInt64(ordinal "ciphertext_bytes")

        if artifactLength < 1L || artifactLength > 200704L then
            corrupt ()

        let expected =
            RecoveryArtifactExportCandidate.encodeWithDigest
                artifactDigest
                artifactLength
                (storedEvidence reader Array.empty)

        if canonical <> expected || SHA256.HashData(canonical) <> digest then
            corrupt ()

        canonical, artifactDigest, digest
