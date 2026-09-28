namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.RecordFormat

[<NoEquality; NoComparison>]
type internal RecoveryArtifactExportEvidence =
    {
        ExportId: Guid
        Artifact: RecoveryArtifactV3
        ExporterActorId: Guid
        ExporterGrantRevision: int64
        ArtifactBytes: byte array
        KeyIssuanceOrdinal: int
        KeyMaximumExports: int
    }

module internal RecoveryArtifactExportCandidate =
    let private outerNames =
        Set.ofList
            [
                "format"
                "formatVersion"
                "keyId"
                "exportId"
                "installationId"
                "epoch"
                "caseId"
                "preparerActorId"
                "preparerGrantRevision"
                "importerActorId"
                "exporterActorId"
                "exporterGrantRevision"
                "operationId"
                "issuedAt"
                "expiresAt"
                "canonicalCommandFormat"
                "requestFingerprintVersion"
                "nonceBase64"
                "ciphertextBase64"
                "tagBase64"
                "macSha256"
            ]

    let exportId
        (installationId: Guid)
        (epoch: int64)
        (operationId: Guid)
        (exporterActorId: Guid)
        (exporterGrantRevision: int64)
        =
        let identity =
            String.Join(
                ":",
                [|
                    "claimcore-recovery-export-v3"
                    installationId.ToString("D")
                    epoch.ToString(CultureInfo.InvariantCulture)
                    operationId.ToString("D")
                    exporterActorId.ToString("D")
                    exporterGrantRevision.ToString(CultureInfo.InvariantCulture)
                |]
            )

        let digest = identity |> Encoding.UTF8.GetBytes |> SHA256.HashData
        Guid(digest.AsSpan(0, 16))

    /// Witness and managed-copy chains retain only identity and ciphertext digest; encrypted
    /// claimant payload remains in the separately owner-prunable artifact payload relation.
    let encodeWithDigest
        (artifactDigest: byte array)
        (artifactLength: int64)
        (evidence: RecoveryArtifactExportEvidence)
        =
        if artifactDigest.Length <> 32 || artifactLength < 1L || artifactLength > 200704L then
            invalidArg (nameof artifactDigest) "Artifact identity is invalid."

        let artifact = evidence.Artifact
        let digest = Convert.ToHexStringLower artifactDigest

        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)

        do
            writer.WriteStartObject()
            writer.WriteString("kind", "RECOVERY_EXPORT_V4")
            writer.WriteString("exportId", evidence.ExportId)
            writer.WriteString("installationId", artifact.InstallationId)
            writer.WriteNumber("epoch", artifact.Epoch)
            writer.WriteString("caseId", artifact.CaseId)
            writer.WriteString("operationId", artifact.OperationId)
            writer.WriteString("preparerActorId", artifact.PreparerActorId)
            writer.WriteNumber("preparerGrantRevision", artifact.PreparerGrantRevision)

            match artifact.ImporterActorId with
            | Some actorId -> writer.WriteString("importerActorId", actorId)
            | None -> writer.WriteNull("importerActorId")

            writer.WriteString("exporterActorId", evidence.ExporterActorId)
            writer.WriteNumber("exporterGrantRevision", evidence.ExporterGrantRevision)
            writer.WriteString("keyId", artifact.KeyId)
            writer.WriteString("nonceBase64", Convert.ToBase64String(artifact.Nonce))

            writer.WriteString(
                "issuedAt",
                artifact.IssuedAt.ToString("O", CultureInfo.InvariantCulture)
            )

            writer.WriteString(
                "expiresAt",
                artifact.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
            )

            writer.WriteString("artifactSha256", digest)
            writer.WriteNumber("artifactBytes", artifactLength)
            writer.WriteNumber("keyIssuanceOrdinal", evidence.KeyIssuanceOrdinal)
            writer.WriteNumber("keyMaximumExports", evidence.KeyMaximumExports)
            writer.WriteEndObject()
            writer.Flush()

        buffer.ToArray()

    let encode (evidence: RecoveryArtifactExportEvidence) =
        evidence
        |> encodeWithDigest
            (SHA256.HashData evidence.ArtifactBytes)
            (int64 evidence.ArtifactBytes.Length)

    /// This is an opaque structural audit only. Witness and owner key custody must separately
    /// prove the independent journal and AES-GCM/HMAC authentication before admission.
    let verifyStored (evidence: RecoveryArtifactExportEvidence) (canonical: byte array) =
        if
            canonical.Length = 0
            || canonical.Length > 300000
            || evidence.ArtifactBytes.Length = 0
            || evidence.ArtifactBytes.Length > 200704
        then
            false
        else
            try
                let expected = encode evidence
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(evidence.ArtifactBytes))
                let root = document.RootElement
                let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                let artifact = evidence.Artifact

                let importerMatches =
                    let value = root.GetProperty("importerActorId")

                    match artifact.ImporterActorId with
                    | None -> value.ValueKind = JsonValueKind.Null
                    | Some id -> value.GetGuid() = id

                CryptographicOperations.FixedTimeEquals(expected, canonical)
                && names.Length = outerNames.Count
                && Set.ofList names = outerNames
                && root.GetProperty("format").GetString() = "claimcore-recovery-artifact"
                && root.GetProperty("formatVersion").GetInt32() = 3
                && root.GetProperty("exportId").GetGuid() = evidence.ExportId
                && root.GetProperty("keyId").GetGuid() = artifact.KeyId
                && root.GetProperty("installationId").GetGuid() = artifact.InstallationId
                && root.GetProperty("epoch").GetInt64() = artifact.Epoch
                && root.GetProperty("caseId").GetGuid() = artifact.CaseId
                && root.GetProperty("operationId").GetGuid() = artifact.OperationId
                && root.GetProperty("preparerActorId").GetGuid() = artifact.PreparerActorId
                && root.GetProperty("preparerGrantRevision").GetInt64() =
                    artifact.PreparerGrantRevision
                && importerMatches
                && root.GetProperty("exporterActorId").GetGuid() = evidence.ExporterActorId
                && root.GetProperty("exporterGrantRevision").GetInt64() =
                    evidence.ExporterGrantRevision
                && root.GetProperty("nonceBase64").GetString() =
                    Convert.ToBase64String(artifact.Nonce)
                && root.GetProperty("issuedAt").GetString() =
                    artifact.IssuedAt.ToString("O", CultureInfo.InvariantCulture)
                && root.GetProperty("expiresAt").GetString() =
                    artifact.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
            with _ ->
                false
