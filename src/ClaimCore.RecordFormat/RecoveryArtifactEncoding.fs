namespace ClaimCore.RecordFormat

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json

/// Caller supplies a fresh nonce and owner-private keys. Exact request bytes are encrypted; only
/// opaque identity and validity metadata remain in the outer authenticated artifact.
[<NoEquality; NoComparison>]
type RecoveryArtifactV3 =
    {
        KeyId: Guid
        ExportId: Guid
        InstallationId: Guid
        Epoch: int64
        CaseId: Guid
        PreparerActorId: Guid
        PreparerGrantRevision: int64
        ImporterActorId: Guid option
        ExporterActorId: Guid
        ExporterGrantRevision: int64
        OperationId: Guid
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        CanonicalCommandFormat: int
        RequestFingerprintVersion: int
        Nonce: byte array
        CanonicalRequest: byte array
    }

module internal RecoveryArtifactEncoding =
    let format = "claimcore-recovery-artifact"
    let version = 3
    let tagLength = 16

    let validKeys (encryptionKey: byte array) (macKey: byte array) =
        encryptionKey.Length = 32
        && macKey.Length = 32
        && not (CryptographicOperations.FixedTimeEquals(encryptionKey, macKey))

    let private validRequest maximumBytes artifact =
        artifact.CanonicalRequest.Length > 0
        && artifact.CanonicalRequest.Length <= maximumBytes
        && (match RequestRecord.decode maximumBytes artifact.CanonicalRequest with
            | Ok request ->
                request.OperationId = artifact.OperationId
                && CryptographicOperations.FixedTimeEquals(
                    RequestRecord.encode request,
                    artifact.CanonicalRequest
                )
            | Error _ -> false)

    let validShape maximumBytes artifact =
        artifact.KeyId <> Guid.Empty
        && artifact.ExportId <> Guid.Empty
        && artifact.InstallationId <> Guid.Empty
        && artifact.Epoch > 0L
        && artifact.CaseId <> Guid.Empty
        && artifact.PreparerActorId <> Guid.Empty
        && artifact.PreparerGrantRevision >= 0L
        && artifact.ImporterActorId <> Some Guid.Empty
        && artifact.ExporterActorId <> Guid.Empty
        && artifact.ExporterGrantRevision >= 0L
        && artifact.OperationId <> Guid.Empty
        && artifact.IssuedAt.Offset = TimeSpan.Zero
        && artifact.ExpiresAt.Offset = TimeSpan.Zero
        && artifact.ExpiresAt > artifact.IssuedAt
        && artifact.CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
        && artifact.RequestFingerprintVersion = RecordVersions.RequestFingerprint
        && artifact.Nonce.Length = 12
        && validRequest maximumBytes artifact

    let id path raw =
        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> Json.reject path "Use a canonical nonempty UUID."

    let moment path raw =
        let mutable value = DateTimeOffset.MinValue

        if
            DateTimeOffset.TryParseExact(
                raw,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &value
            )
            && value.Offset = TimeSpan.Zero
            && value.ToString("O", CultureInfo.InvariantCulture) = raw
        then
            value
        else
            Json.reject path "Use one canonical UTC instant."

    let base64 path expectedLength raw =
        let bytes =
            try
                Convert.FromBase64String(raw)
            with :? FormatException ->
                Json.reject path "The encoded bytes are invalid."

        if bytes.Length <> expectedLength || Convert.ToBase64String(bytes) <> raw then
            Json.reject path "The encoded bytes are not canonical."

        bytes

    let private writeMetadata (writer: Utf8JsonWriter) (artifact: RecoveryArtifactV3) =
        writer.WriteString("format", format)
        writer.WriteNumber("formatVersion", version)
        writer.WriteString("keyId", artifact.KeyId)
        writer.WriteString("exportId", artifact.ExportId)
        writer.WriteString("installationId", artifact.InstallationId)
        writer.WriteNumber("epoch", artifact.Epoch)
        writer.WriteString("caseId", artifact.CaseId)
        writer.WriteString("preparerActorId", artifact.PreparerActorId)
        writer.WriteNumber("preparerGrantRevision", artifact.PreparerGrantRevision)

        match artifact.ImporterActorId with
        | Some value -> writer.WriteString("importerActorId", value)
        | None -> writer.WriteNull("importerActorId")

        writer.WriteString("exporterActorId", artifact.ExporterActorId)
        writer.WriteNumber("exporterGrantRevision", artifact.ExporterGrantRevision)

        writer.WriteString("operationId", artifact.OperationId)

        writer.WriteString(
            "issuedAt",
            artifact.IssuedAt.ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteString(
            "expiresAt",
            artifact.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteNumber("canonicalCommandFormat", artifact.CanonicalCommandFormat)
        writer.WriteNumber("requestFingerprintVersion", artifact.RequestFingerprintVersion)

    let metadata artifact =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writeMetadata writer artifact
            writer.WriteEndObject())

    let outer artifact ciphertext tag (mac: string option) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writeMetadata writer artifact
            writer.WriteString("nonceBase64", Convert.ToBase64String(artifact.Nonce))
            writer.WriteString("ciphertextBase64", Convert.ToBase64String(ciphertext))
            writer.WriteString("tagBase64", Convert.ToBase64String(tag))
            mac |> Option.iter (fun value -> writer.WriteString("macSha256", value))
            writer.WriteEndObject())

    let plain (artifact: RecoveryArtifactV3) =
        let digest =
            artifact.CanonicalRequest |> SHA256.HashData |> Convert.ToHexStringLower

        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("requestSha256", digest)

            writer.WriteString(
                "canonicalRequestBase64",
                Convert.ToBase64String(artifact.CanonicalRequest)
            )

            writer.WriteEndObject())
